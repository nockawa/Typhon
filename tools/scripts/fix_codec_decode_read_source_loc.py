"""For codecs whose Decode methods compute hasSourceLocation but don't actually read
the source-loc id, add the read + thread it through to the *Data ctor.

Pattern matched (Decode method):
    var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
    var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
    var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];

Becomes:
    var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
    var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
    ushort sourceLocationId = 0;
    if (hasSourceLocation)
    {
        sourceLocationId = TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset(hasTC)..]);
    }
    var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSourceLocation)..];

Then any 'return new XxxData(...);' inside that method gets ', sourceLocationId' appended
before the trailing );

This is for span codecs whose *Data structs DO have a SourceLocationId field but Decode
didn't propagate it. Idempotent.
"""

import re
import sys
from pathlib import Path


PATTERN = re.compile(
    r'(?P<head>(?P<indent>[ \t]+)var (?P<htc>hasTC|hasTraceContext) = \(spanFlags & TraceRecordHeader\.SpanFlagsHasTraceContext\) != 0;\s*\n'
    r'\s*var hasSourceLocation = \(spanFlags & TraceRecordHeader\.SpanFlagsHasSourceLocation\) != 0;\s*\n)'
    r'(?P<peek>\s*var (?:p|payload) = source\[)',
    re.MULTILINE,
)

INSERT_TEMPLATE = (
    "{indent}ushort sourceLocationId = 0;\n"
    "{indent}if (hasSourceLocation)\n"
    "{indent}{{\n"
    "{indent}    sourceLocationId = TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset({htc})..]);\n"
    "{indent}}}\n"
)


def fix_decode_inserts(text: str) -> tuple[str, int]:
    count = 0
    out = []
    cursor = 0
    for m in PATTERN.finditer(text):
        out.append(text[cursor:m.start()])
        out.append(m.group("head"))
        # Skip if next non-ws line already declares sourceLocationId
        rest = text[m.end("head"):]
        # Look ahead a few lines
        if "ushort sourceLocationId = 0;" in rest[:200]:
            out.append(text[m.end("head"):m.end()])
        else:
            indent = m.group("indent")
            out.append(INSERT_TEMPLATE.format(indent=indent, htc=m.group("htc")))
            out.append(text[m.end("head"):m.end()])
            count += 1
        cursor = m.end()
    out.append(text[cursor:])
    return "".join(out), count


# Append ', sourceLocationId' to last argument of return new XxxData(...) where the
# ctor is for a struct that has SourceLocationId.
RETURN_NEW_RE = re.compile(
    r'(return new (?P<ty>\w+Data)\([^()]*?(?:\([^()]*?\)[^()]*?)*)\);',
    re.DOTALL,
)


def append_srcloc_to_returns(text: str) -> tuple[str, int]:
    # Find which Data structs have SourceLocationId in this file
    has_field_types = set()
    for m in re.finditer(r'public readonly struct (\w+Data)\s*\n\{(.*?)\n\}', text, re.DOTALL):
        if "SourceLocationId" in m.group(2):
            has_field_types.add(m.group(1))

    count = 0

    def repl(m: re.Match) -> str:
        nonlocal count
        ty = m.group("ty")
        if ty not in has_field_types:
            return m.group(0)
        head = m.group(1)
        # Skip if already has sourceLocationId/srcLoc as the last arg
        last_700 = head[-300:]
        if 'sourceLocationId)' in last_700 or 'sourceLocationId,' in last_700 or 'srcLoc)' in last_700 or ', sourceLocationId' in last_700:
            return m.group(0)
        count += 1
        return head + ", sourceLocationId);"

    new_text = RETURN_NEW_RE.sub(repl, text)
    return new_text, count


def main() -> int:
    files = sys.argv[1:] or list(Path("src/Typhon.Profiler").glob("*.cs"))
    total_inserts = 0
    total_returns = 0
    for f in files:
        path = Path(f)
        text = path.read_text(encoding="utf-8")
        text, n1 = fix_decode_inserts(text)
        text, n2 = append_srcloc_to_returns(text)
        if n1 or n2:
            path.write_text(text, encoding="utf-8")
            print(f"  {path.name}: inserts={n1}, return-new appended={n2}")
            total_inserts += n1
            total_returns += n2
    print(f"\nTotal inserts: {total_inserts}, return-new appended: {total_returns}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
