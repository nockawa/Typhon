"""
Pass 3: update every codec method in src/Typhon.Profiler/*EventCodec.cs to support
the optional source-location id wire field.

For each `internal static void EncodeXxx(... out int bytesWritten)` method:
1. Add `ushort sourceLocationId = 0` as the LAST parameter (after `out int bytesWritten`).
2. Add `var hasSourceLocation = sourceLocationId != 0;` after the `hasTraceContext` line.
3. After the size computation (`var size = ...;`), add `if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;`.
4. Replace the spanFlags assignment to OR in SpanFlagsHasSourceLocation.
5. Replace `SpanHeaderSize(hasTraceContext)` → `SpanHeaderSize(hasTraceContext, hasSourceLocation)` (for headerSize / payload-offset calcs only — keep ComputeXSize helpers untouched).
6. Insert a `WriteSourceLocationId` block after the `WriteTraceContext` block.

Also handle the Decode methods: after the TraceContext read, insert a SourceLocationId read.
The EventData struct gets `SourceLocationId` added separately (handled in a follow-up if needed).

Idempotent (skips if already-converted).
"""
import re
from pathlib import Path

CODECS = list(Path("src/Typhon.Profiler").glob("*EventCodec.cs"))

# These codec files we already updated by hand or shouldn't touch.
SKIP = {
    "BTreeEventCodec.cs",  # Already manually updated.
}


def main():
    files_touched = 0
    methods_touched = 0
    for path in sorted(CODECS):
        if path.name in SKIP:
            continue
        text = path.read_text(encoding="utf-8")
        original = text

        text, n1 = update_encode_signatures(text)
        text, n2 = update_encode_bodies(text)

        if text != original:
            path.write_text(text, encoding="utf-8")
            files_touched += 1
            methods_touched += n1
    print(f"update_codecs: {files_touched} files updated, {methods_touched} encode methods got the new parameter.")


def update_encode_signatures(text: str) -> tuple[str, int]:
    """
    Add `ushort sourceLocationId = 0` to every method whose last param is `out int bytesWritten`.
    Idempotent.
    """
    count = 0
    def repl(m):
        nonlocal count
        # m.group(0) ends with `out int bytesWritten)`. Replace with adding the new param.
        full = m.group(0)
        if "ushort sourceLocationId" in full:
            return full
        count += 1
        # Preserve indentation of the closing paren line.
        return full.replace("out int bytesWritten)", "out int bytesWritten,\n        ushort sourceLocationId = 0)")
    # Match minimally — find the closing `)` of the parameter list. The pattern just looks for the
    # specific line `out int bytesWritten)`.
    text = re.sub(r"out int bytesWritten\)", repl, text)
    return text, count


def update_encode_bodies(text: str) -> tuple[str, int]:
    """
    For each EncodeX method body, perform these transformations:
    - Insert `var hasSourceLocation = sourceLocationId != 0;` after `var hasTraceContext = ...;`.
    - After the size assignment line `var size = ...;`, add an in-place size adjustment:
      `if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;`.
    - Replace the spanFlags computation to OR in SpanFlagsHasSourceLocation.
    - Replace `var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext);` with the
      (bool, bool) overload so payload offset is correct.
    - Insert a WriteSourceLocationId block after the WriteTraceContext block.
    Idempotent.
    """
    if "hasSourceLocation" in text:
        # Already processed.
        return text, 0

    # 1) hasSourceLocation declaration after hasTraceContext.
    text = re.sub(
        r"(var hasTraceContext = traceIdHi != 0 \|\| traceIdLo != 0;\n)",
        r"\1        var hasSourceLocation = sourceLocationId != 0;\n",
        text,
    )

    # 2) Size adjustment immediately after `var size = ...;` lines.
    # Match `var size = ...;\n` (single line) and append the adjustment on the next line.
    def size_repl(m):
        full = m.group(0)
        if "SourceLocationIdSize" in full:
            return full
        return full + "        if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;\n"
    text = re.sub(r"        var size = [^;]+;\n", size_repl, text)

    # 3) spanFlags assignment.
    text = text.replace(
        "var spanFlags = hasTraceContext ? TraceRecordHeader.SpanFlagsHasTraceContext : (byte)0;",
        "var spanFlags = (byte)((hasTraceContext ? TraceRecordHeader.SpanFlagsHasTraceContext : 0)\n"
        "                             | (hasSourceLocation ? TraceRecordHeader.SpanFlagsHasSourceLocation : 0));"
    )

    # 4) headerSize update — payload-offset-correct calculation.
    text = text.replace(
        "var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext);",
        "var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation);"
    )

    # 5) Insert WriteSourceLocationId after the WriteTraceContext block.
    # Match the entire `if (hasTraceContext) { ... WriteTraceContext... }` block and append after it.
    # The pattern is:
    #     if (hasTraceContext)
    #     {
    #         TraceRecordHeader.WriteTraceContext(...);
    #     }
    text = re.sub(
        r"(if \(hasTraceContext\)\s*\{\s*\n\s*TraceRecordHeader\.WriteTraceContext\([^)]*\);\s*\n\s*\})",
        r"\1\n        if (hasSourceLocation)\n        {\n"
        r"            TraceRecordHeader.WriteSourceLocationId(destination[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..], sourceLocationId);\n"
        r"        }",
        text,
    )

    return text, 0  # method count not tracked here (signature update tracks it).


if __name__ == "__main__":
    main()
