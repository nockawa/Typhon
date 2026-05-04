"""
Pass 2: inject `SourceLocationId = siteId,` into every BeginXxxWithSiteId factory body
(skipping factories that already have the field set, like BeginBTreeInsertWithSiteId).

Also adds:
- `internal ushort SourceLocationId;` field to every `*Event` ref struct in src/Typhon.Engine/Profiler/Events/
- updates each `ComputeSize()` to use the SourceLocationId-aware overload
- updates each `EncodeTo(...)` to pass SourceLocationId to the codec helper

Idempotent — re-running is safe.
"""
import re
import sys
from pathlib import Path

ENGINE = Path("src/Typhon.Engine")
TYPHON_EVENT = ENGINE / "Profiler/TyphonEvent.cs"
EVENTS_DIR = ENGINE / "Profiler/Events"


def main():
    inject_into_typhon_event()
    update_event_ref_structs()


# ─── Pass 2a: TyphonEvent.cs ─────────────────────────────────────────────────

def inject_into_typhon_event():
    text = TYPHON_EVENT.read_text(encoding="utf-8")
    lines = text.split("\n")
    out = []
    i = 0
    n = len(lines)
    injected = 0
    skipped = 0
    while i < n:
        line = lines[i]
        m = re.match(r"^\s*public static \w+Event (Begin\w+)WithSiteId\(", line)
        if m:
            # Walk forward to find the body's `return new ...` initializer's closing `};`
            # and inject before it. If `SourceLocationId` is already in the body, skip.
            body_start = i
            depth = 0
            return_block_close = None
            in_return_block = False
            already_has_field = False
            for j in range(i, n):
                ln = lines[j]
                if "SourceLocationId" in ln:
                    already_has_field = True
                if not in_return_block and re.match(r"^\s*return new \w+Event\s*$", ln):
                    in_return_block = True
                if in_return_block and ln.strip().startswith("};"):
                    return_block_close = j
                    break
                # Track factory's outer brace depth for fallback termination.
                for c in ln:
                    if c == "{":
                        depth += 1
                    elif c == "}":
                        depth -= 1
                        if depth == 0 and j > body_start:
                            break
                if depth == 0 and j > body_start and ln.strip().endswith("}"):
                    break

            if already_has_field or return_block_close is None:
                if already_has_field:
                    skipped += 1
                out.append(line)
                i += 1
                continue

            # Emit lines up to (but not including) the closing `};`
            for k in range(i, return_block_close):
                out.append(lines[k])
            # Determine the indent from the previous initializer field.
            field_indent = "            "
            for prev in reversed(out):
                pm = re.match(r"^(\s+)\w+ = .*,\s*$", prev)
                if pm:
                    field_indent = pm.group(1)
                    break
            out.append(f"{field_indent}SourceLocationId = siteId,")
            out.append(lines[return_block_close])  # the `};` line
            injected += 1
            i = return_block_close + 1
            continue
        out.append(line)
        i += 1
    TYPHON_EVENT.write_text("\n".join(out), encoding="utf-8")
    print(f"TyphonEvent.cs: injected SourceLocationId in {injected} WithSiteId factories, "
          f"{skipped} already had it.")


# ─── Pass 2b: ref struct field + ComputeSize + EncodeTo ──────────────────────

# Most ref structs follow one of two ComputeSize patterns:
#   (a) compact: `public readonly int ComputeSize() => TraceRecordHeader.SpanHeaderSize(TraceIdHi != 0 || TraceIdLo != 0);`
#   (b) blocky:  has `var hasTraceContext = ...; return TraceRecordHeader.SpanHeaderSize(hasTraceContext);`
#
# EncodeTo passes (kind, ThreadSlot, StartTimestamp, SpanId, ParentSpanId, TraceIdHi, TraceIdLo, out bytesWritten)
# to a codec helper. We append `, SourceLocationId` at the end of the args list.


def update_event_ref_structs():
    files_touched = 0
    structs_touched = 0
    for path in sorted(EVENTS_DIR.glob("*.cs")):
        if path.name == "ITraceEventEncoder.cs":
            continue
        text = path.read_text(encoding="utf-8")
        original = text

        # 1) Insert `internal ushort SourceLocationId;` after the last public field of each ref struct.
        text, count_field = inject_field(text)
        # 2) Update ComputeSize to use the SourceLocationId-aware overload.
        text, count_size = update_compute_size(text)
        # 3) Update EncodeTo(...) to pass SourceLocationId.
        text, count_encode = update_encode_to(text)

        if text != original:
            path.write_text(text, encoding="utf-8")
            files_touched += 1
            structs_touched += count_field
    print(f"Events: {files_touched} files updated, {structs_touched} ref structs got a SourceLocationId field.")


def inject_field(text: str) -> tuple[str, int]:
    """
    For each `public ref struct XEvent : ITraceEventEncoder { ... }`, insert
    `internal ushort SourceLocationId;` after the last `public ulong TraceIdLo;` field
    (which is the canonical last shared field across all event ref structs).
    Skip structs that already have the field.
    """
    count = 0
    def repl(m):
        nonlocal count
        struct_body = m.group(0)
        if "SourceLocationId" in struct_body:
            return struct_body
        # Inject after `public ulong TraceIdLo;`.
        new_body = re.sub(
            r"(public ulong TraceIdLo;\s*\n)",
            r"\1    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>\n    internal ushort SourceLocationId;\n",
            struct_body,
            count=1,
        )
        if new_body != struct_body:
            count += 1
        return new_body

    # Match each ref struct from declaration to its closing brace (single-pass per struct).
    # Use a non-greedy match that stops at the first `}` at column 0 (ref structs are top-level here).
    pattern = re.compile(
        r"public ref struct \w+Event\s*:\s*ITraceEventEncoder\s*\{[^}]*?\n\}",
        re.DOTALL,
    )
    text = pattern.sub(repl, text)
    return text, count


def update_compute_size(text: str) -> tuple[str, int]:
    """
    Convert:
      public readonly int ComputeSize() => TraceRecordHeader.SpanHeaderSize(TraceIdHi != 0 || TraceIdLo != 0);
    →
      public readonly int ComputeSize() => TraceRecordHeader.SpanHeaderSize(TraceIdHi != 0 || TraceIdLo != 0, SourceLocationId != 0);

    And the blocky variant:
      var hasTraceContext = TraceIdHi != 0 || TraceIdLo != 0;
      return TraceRecordHeader.SpanHeaderSize(hasTraceContext);
    →
      ... return TraceRecordHeader.SpanHeaderSize(hasTraceContext, SourceLocationId != 0);
    """
    count = 0

    # Compact form on one line.
    def compact_repl(m):
        nonlocal count
        if "SourceLocationId" in m.group(0):
            return m.group(0)
        count += 1
        return m.group(1) + ", SourceLocationId != 0" + m.group(2)

    text = re.sub(
        r"(TraceRecordHeader\.SpanHeaderSize\(TraceIdHi != 0 \|\| TraceIdLo != 0)(\))",
        compact_repl,
        text,
    )
    # Blocky form using `hasTraceContext` local.
    text = re.sub(
        r"(return TraceRecordHeader\.SpanHeaderSize\(hasTraceContext)(\);)",
        compact_repl,
        text,
    )
    return text, count


def update_encode_to(text: str) -> tuple[str, int]:
    """
    Append `, SourceLocationId` before the `out bytesWritten` argument in each EncodeTo's codec call.

    The shape (with line breaks) is roughly:
        => XxxEventCodec.EncodeXxx(destination, endTimestamp, ..., TraceIdHi, TraceIdLo, out bytesWritten);

    We add ", SourceLocationId" right before "out bytesWritten" — but only on the call site that
    immediately precedes "out bytesWritten" inside an `EncodeTo(...)` body. Skip if already present.
    """
    count = 0
    def repl(m):
        nonlocal count
        full = m.group(0)
        # Check the surrounding ~200 chars to avoid double-injection on a re-run.
        # The codec call args directly precede `, out bytesWritten`. If we already have
        # `, SourceLocationId,` just before `, out bytesWritten`, skip.
        if "SourceLocationId, out bytesWritten" in full:
            return full
        count += 1
        return full.replace(", out bytesWritten", ", out bytesWritten, SourceLocationId")

    # Match each EncodeTo body (single expression-bodied or block-bodied).
    # Simplest: globally rewrite all `, out bytesWritten);` occurrences inside this file. Each is in
    # an EncodeTo call. Idempotency check inside repl prevents double-injection.
    text = re.sub(
        r"[^;]*?, out bytesWritten\);",
        repl,
        text,
        flags=re.DOTALL,
    )
    return text, count


if __name__ == "__main__":
    main()
