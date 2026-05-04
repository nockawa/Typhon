"""
Generate golden-bytes test methods for every event ref struct in
src/Typhon.Engine/Profiler/Events/.

Output: a C# fragment to be pasted between the existing `// ─────...` markers
in test/Typhon.Engine.Tests/Profiler/TraceEventEncodeEquivalenceTests.cs.

Strategy: for each struct, parse its public fields, optional properties (with
their mask bits), and the EncodeTo lambda's codec call. Generate a test that:
  1. Object-initializes the struct with deterministic sentinels for all settable
     fields (incl. optional props -> setters flip mask bits).
  2. Calls struct.EncodeTo into bufStruct.
  3. Calls the codec method directly with the same sentinels (substituting
     `_optMask` -> expectedMask, `_<backing>` -> per-prop sentinel).
  4. Asserts the two byte spans are equal.
"""

import re
import sys
from pathlib import Path

EVENTS_DIR = Path("/Users/loic/RiderProjects/Typhon/src/Typhon.Engine/Profiler/Events")

HEADER_FIELDS = {"ThreadSlot", "StartTimestamp", "SpanId", "ParentSpanId",
                 "PreviousSpanId", "TraceIdHi", "TraceIdLo",
                 # Post-Phase-1: the seven flat fields are consolidated into a
                 # single embedded TraceSpanHeader struct named "Header". Treat
                 # it as a header field so it is not emitted as a payload.
                 "Header"}
# Header-field names that appear in EncodeTo args verbatim and map to the
# corresponding test constants.
ARG_NAME_TO_CONST = {
    "ThreadSlot": "ThreadSlot",
    "StartTimestamp": "StartTs",
    "SpanId": "SpanId",
    "ParentSpanId": "ParentSpanId",
    "TraceIdHi": "TraceIdHi",
    "TraceIdLo": "TraceIdLo",
    "destination": "bufCodec",
    "endTimestamp": "EndTs",
    "bytesWritten": "lenCodec",
}

PRIMITIVES = {"byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong",
              "bool", "float", "double"}


class StructInfo:
    def __init__(self, name, kind):
        self.name = name
        self.kind = kind
        self.public_fields = []     # list of (type, name) — incl. header
        self.optional_props = []    # list of (type, prop_name, backing_field, mask_const)
        self.codec_class = None
        self.codec_method = None
        self.codec_args = None

    def payload_fields(self):
        return [(t, n) for (t, n) in self.public_fields if n not in HEADER_FIELDS]


def parse_struct(text):
    """Parse a single ref-struct body. Returns StructInfo or None.

    Post-Phase-2 layout:
        [TraceEvent(TraceEventKind.<Kind>, Codec = typeof(<Codec>), ...)]
        public ref partial struct <Name>
        {
            [BeginParam] public T Field1;          // (or just `public T Field1;`)
            [Optional]   private T _backing;        // optional field
            public readonly int ComputeSize() => ...;
            public readonly void EncodeTo(...) => <Codec>.<Method>(...);
        }
    """
    # Find the [TraceEvent(...)] attribute right before the struct decl.
    attr_re = re.search(
        r"\[TraceEvent\(TraceEventKind\.(?P<kind>\w+)(?P<rest>[^\]]*)\)\]\s*"
        r"public ref partial struct (?P<name>\w+Event)\s*\{(?P<body>.*)",
        text, re.DOTALL,
    )
    if not attr_re:
        return None
    name = attr_re.group("name")
    kind = attr_re.group("kind")
    body = attr_re.group("body")
    info = StructInfo(name, kind)

    # public fields (incl [BeginParam]-tagged). Skip `Header` (added by generator anyway).
    for fm in re.finditer(r"^[ \t]*(?:\[\w+(?:\([^)]*\))?\]\s*)*public\s+(\w+(?:\?|<[^>]+>)?)\s+(\w+);",
                          body, re.MULTILINE):
        typ, fname = fm.group(1), fm.group(2)
        if fname == "Header":
            continue
        info.public_fields.append((typ, fname))

    # [Optional] private fields. Mask constant follows convention "Opt" + PropertyName.
    for om in re.finditer(r"\[Optional(?:\([^)]*\))?\]\s*private\s+(\w+(?:\?)?)\s+(_\w+);",
                          body):
        typ = om.group(1)
        backing = om.group(2)
        prop = backing.lstrip("_")
        prop = prop[0].upper() + prop[1:] if prop else prop
        # Codec class is in the [TraceEvent] attribute's `rest`.
        codec_m = re.search(r"Codec\s*=\s*typeof\((\w+)\)", attr_re.group("rest"))
        codec_class = codec_m.group(1) if codec_m else None
        mask_const = f"{codec_class}.Opt{prop}" if codec_class else f"Opt{prop}"
        info.optional_props.append((typ, prop, backing, mask_const))

    em = re.search(r"public readonly void EncodeTo\([^{]*?\)\s*=>\s*(\w+)\.(\w+)\((.*?)\);",
                   body, re.DOTALL)
    if em:
        info.codec_class = em.group(1)
        info.codec_method = em.group(2)
        info.codec_args = re.sub(r"\s+", " ", em.group(3).strip())
    return info


# Sentinel value pools — index by field index within struct (cycle).
def sentinel(typ, idx, fname=""):
    """Deterministic sentinel value for a field of the given type. `idx` is the
    1-based position within the struct, used to make adjacent fields distinct.
    """
    # Some enums show up as their type name; we cast (Enum)1 by default. For
    # integer-typed fields we use idx-derived values.
    if typ == "bool":
        return "true"
    if typ == "byte":
        return f"(byte)0x{(0x10 + idx):02X}"
    if typ == "sbyte":
        return f"(sbyte){idx}"
    if typ == "ushort":
        return f"(ushort)0x{(0x1100 + idx * 0x100):04X}"
    if typ == "short":
        return f"(short){100 * idx}"
    if typ == "int":
        return f"{100 * idx + idx}"
    if typ == "uint":
        return f"{1000 * idx + idx}u"
    if typ == "long":
        return f"{1000 * idx + idx}L"
    if typ == "ulong":
        return f"0x{(0xAA000000 + idx * 0x010101):08X}UL"
    if typ == "float":
        return f"{idx}.5f"
    if typ == "double":
        return f"{idx}.25"
    # Otherwise treat as enum — cast int to its type. We use 1-based idx, modulo
    # 4 to avoid hitting unknown enum values for tightly-numbered enums.
    return f"({typ}){((idx - 1) % 4) + 1}"


def load_goldens():
    """Load goldens from the side file written during the codec-equivalence run."""
    path = Path("/tmp/goldens.txt")
    if not path.exists():
        return {}
    out = {}
    for line in path.read_text().splitlines():
        if line.startswith("GOLDEN:"):
            _, name, hexbytes = line.split(":", 2)
            out[name.strip()] = hexbytes.strip()
    return out


GOLDENS = load_goldens()


def gen_test(info: StructInfo) -> str:
    """Emit a [Test] method as a C# string."""
    lines = []
    lines.append("    [Test]")
    lines.append(f"    public void {info.name}_StructEncode_MatchesCodec()")
    lines.append("    {")

    # Walk payload fields and optional props, build sentinel mapping.
    sentinels = {}      # name -> literal expression
    backing_to_value = {}  # backing-field name (e.g. "_optMask") -> literal

    # Payload (public non-header) fields.
    payload = info.payload_fields()
    for i, (typ, fname) in enumerate(payload, start=1):
        sentinels[fname] = sentinel(typ, i, fname)

    # Optional properties — number them after payload to avoid collisions.
    for j, (typ, pname, backing, mask) in enumerate(info.optional_props,
                                                    start=len(payload) + 1):
        sentinels[pname] = sentinel(typ, j, pname)
        backing_to_value[backing] = sentinels[pname]

    # NOTE: previous versions emitted `var expectedMask = ...` here. After
    # Phase 1 the test asserts against frozen golden hex (not against a recomputed
    # codec call), so the variable was dead and tripped CS0219. Removed.

    # Object initializer. Post-Phase-1, the seven prologue fields are embedded
    # as a TraceSpanHeader sub-struct rather than top-level fields.
    lines.append(f"        var ev = new {info.name}")
    lines.append("        {")
    lines.append("            Header = new TraceSpanHeader")
    lines.append("            {")
    lines.append("                ThreadSlot = ThreadSlot,")
    lines.append("                StartTimestamp = StartTs,")
    lines.append("                SpanId = SpanId,")
    lines.append("                ParentSpanId = ParentSpanId,")
    lines.append("                TraceIdHi = TraceIdHi,")
    lines.append("                TraceIdLo = TraceIdLo,")
    lines.append("            },")
    for typ, fname in payload:
        lines.append(f"            {fname} = {sentinels[fname]},")
    for typ, pname, backing, mask in info.optional_props:
        lines.append(f"            {pname} = {sentinels[pname]},")
    lines.append("        };")

    lines.append("")
    lines.append("        Span<byte> bufStruct = stackalloc byte[256];")
    lines.append("        ev.EncodeTo(bufStruct, EndTs, out var lenStruct);")
    lines.append("")

    golden_hex = GOLDENS.get(info.name)
    if golden_hex is None:
        # Fall back to a placeholder; the build will surface missing goldens.
        golden_hex = "DEADBEEF"
    lines.append(f"        var golden = Convert.FromHexString(\"{golden_hex}\");")
    lines.append("        AssertSpanEqualsGolden(bufStruct, lenStruct, golden);")
    lines.append("    }")
    return "\n".join(lines) + "\n"


def main():
    structs = []
    for cs_file in sorted(EVENTS_DIR.glob("*.cs")):
        if cs_file.name == "ITraceEventEncoder.cs":
            continue
        text = cs_file.read_text()
        for part in re.split(r"(?=\[TraceEvent\(TraceEventKind\.\w+)", text):
            if "[TraceEvent(" not in part:
                continue
            info = parse_struct(part)
            if info and info.codec_class:
                structs.append(info)

    print(f"// Generated {len(structs)} test methods", file=sys.stderr)

    out = []
    for s in structs:
        out.append(gen_test(s))

    print("\n".join(out))


if __name__ == "__main__":
    main()
