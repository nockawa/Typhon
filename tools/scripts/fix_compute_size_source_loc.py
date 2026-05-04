"""Fix expression-bodied ComputeSize methods in ref structs that delegate to codec
helpers without accounting for SourceLocationIdSize. Converts them to multi-statement
bodies that add the size when SourceLocationId != 0."""

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
EVENTS_DIR = ROOT / "src" / "Typhon.Engine" / "Profiler" / "Events"

# Match: optional whitespace, "public readonly int ComputeSize() => <codec call>(<arg>);"
# Group 1: indentation
# Group 2: full call expression (everything between "=> " and ";")
PATTERN = re.compile(
    r'^(?P<indent>[ \t]+)public readonly int ComputeSize\(\) => (?P<call>[^;]+);\s*$',
    re.MULTILINE,
)

REPLACEMENT_TEMPLATE = (
    "{indent}public readonly int ComputeSize()\n"
    "{indent}{{\n"
    "{indent}    var s = {call};\n"
    "{indent}    if (SourceLocationId != 0) s += TraceRecordHeader.SourceLocationIdSize;\n"
    "{indent}    return s;\n"
    "{indent}}}"
)


def file_has_source_loc(text: str) -> bool:
    return "internal ushort SourceLocationId;" in text


def fix_file(path: Path) -> int:
    text = path.read_text(encoding="utf-8")
    if not file_has_source_loc(text):
        return 0

    count = 0

    def repl(m: re.Match) -> str:
        nonlocal count
        call = m.group("call")
        # Skip if already accounts for SourceLocationId (2-arg SpanHeaderSize form)
        if "SourceLocationId" in call:
            return m.group(0)
        count += 1
        return REPLACEMENT_TEMPLATE.format(indent=m.group("indent"), call=call)

    new_text = PATTERN.sub(repl, text)
    if count > 0:
        path.write_text(new_text, encoding="utf-8")
    return count


def main() -> int:
    total = 0
    for cs_file in sorted(EVENTS_DIR.glob("*.cs")):
        n = fix_file(cs_file)
        if n:
            print(f"  {cs_file.relative_to(ROOT)}: {n} method(s) fixed")
            total += n
    print(f"\nTotal: {total} ComputeSize methods fixed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
