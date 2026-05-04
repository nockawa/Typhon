"""Add source-loc decode logic to every Decode method that currently reads
trace context but ignores source-loc.

Pattern matched:
    ulong traceIdHi = 0, traceIdLo = 0;
    var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
    if (hasTC) { ReadTraceContext(...); }
    var p = source[TraceRecordHeader.SpanHeaderSize(hasTC)..];

Transformed to:
    ulong traceIdHi = 0, traceIdLo = 0;
    ushort sourceLocationId = 0;
    var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
    var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
    if (hasTC) { ReadTraceContext(...); }
    if (hasSourceLocation) { sourceLocationId = ReadSourceLocationId(...); }
    var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];

Also handles:
    var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
    ulong traceIdHi = 0, traceIdLo = 0;

Idempotent.

This is for the codec Encode side too, where it's symmetric to Decode but for source[..] becoming destination[..].
"""

import re
import sys
from pathlib import Path

DECODE_PATTERN = re.compile(
    r'(\s*)ulong traceIdHi = 0, traceIdLo = 0;\s*\n'
    r'(\s*)var hasTC = \(spanFlags & TraceRecordHeader\.SpanFlagsHasTraceContext\) != 0;\s*\n'
    r'(\s*)if \(hasTC\)\s*\n'
    r'(\s*)\{\s*\n'
    r'(\s*)TraceRecordHeader\.ReadTraceContext\(source\[TraceRecordHeader\.MinSpanHeaderSize\.\.\], out traceIdHi, out traceIdLo\);\s*\n'
    r'(\s*)\}\s*\n'
    r'(\s*)var p = source\[TraceRecordHeader\.SpanHeaderSize\(hasTC\)\.\.\];',
    re.MULTILINE,
)

DECODE_PATTERN_2 = re.compile(
    r'(\s*)var hasTC = \(spanFlags & TraceRecordHeader\.SpanFlagsHasTraceContext\) != 0;\s*\n'
    r'(\s*)ulong traceIdHi = 0, traceIdLo = 0;\s*\n'
    r'(\s*)if \(hasTC\)\s*\n'
    r'(\s*)\{\s*\n'
    r'(\s*)TraceRecordHeader\.ReadTraceContext\(source\[TraceRecordHeader\.MinSpanHeaderSize\.\.\], out traceIdHi, out traceIdLo\);\s*\n'
    r'(\s*)\}\s*\n'
    r'(\s*)var p = source\[TraceRecordHeader\.SpanHeaderSize\(hasTC\)\.\.\];',
    re.MULTILINE,
)

REPLACEMENT = (
    r'\1ulong traceIdHi = 0, traceIdLo = 0;'
    "\n"
    r'\2ushort sourceLocationId = 0;'
    "\n"
    r'\2var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;'
    "\n"
    r'\2var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;'
    "\n"
    r'\3if (hasTC)'
    "\n"
    r'\4{'
    "\n"
    r'\5TraceRecordHeader.ReadTraceContext(source[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);'
    "\n"
    r'\6}'
    "\n"
    r'\3if (hasSourceLocation)'
    "\n"
    r'\4{'
    "\n"
    r'\5sourceLocationId = TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset(hasTC)..]);'
    "\n"
    r'\6}'
    "\n"
    r'\7var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];'
)

REPLACEMENT_2 = (
    r'\1var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;'
    "\n"
    r'\2var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;'
    "\n"
    r'\2ulong traceIdHi = 0, traceIdLo = 0;'
    "\n"
    r'\2ushort sourceLocationId = 0;'
    "\n"
    r'\3if (hasTC)'
    "\n"
    r'\4{'
    "\n"
    r'\5TraceRecordHeader.ReadTraceContext(source[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);'
    "\n"
    r'\6}'
    "\n"
    r'\3if (hasSourceLocation)'
    "\n"
    r'\4{'
    "\n"
    r'\5sourceLocationId = TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset(hasTC)..]);'
    "\n"
    r'\6}'
    "\n"
    r'\7var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];'
)


def fix_file(path: Path) -> int:
    text = path.read_text(encoding="utf-8")
    new_text = text
    n1 = 0
    new_text, n1 = DECODE_PATTERN.subn(REPLACEMENT, new_text)
    n2 = 0
    new_text, n2 = DECODE_PATTERN_2.subn(REPLACEMENT_2, new_text)
    if n1 + n2:
        path.write_text(new_text, encoding="utf-8")
    return n1 + n2


def main() -> int:
    profiler_dir = Path(__file__).resolve().parents[2] / "src" / "Typhon.Profiler"
    total = 0
    for cs in sorted(profiler_dir.glob("*.cs")):
        n = fix_file(cs)
        if n:
            print(f"  {cs.name}: {n} Decode method(s) fixed")
            total += n
    print(f"\nTotal: {total} Decode methods fixed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
