"""For every codec method that does:
    var size = ComputeSize<X>(<args>);
    <stmt that is NOT 'if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;'>

…insert the missing fix line right after the var size assignment, and ensure
'var hasSourceLocation = sourceLocationId != 0;' exists in the same method scope.

Mirrors the canonical pattern in CheckpointEventCodec.EncodeCycle:

    var hasTraceContext = traceIdHi != 0 || traceIdLo != 0;
    var hasSourceLocation = sourceLocationId != 0;
    var size = ComputeCycleSize(hasTraceContext, optMask);
    if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;

The script only mutates files under src/Typhon.Profiler/. It skips any var-size
line that already has the fix on the immediately-following line, and skips
methods that don't take 'ushort sourceLocationId' (no work to do).
"""

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PROFILER_DIR = ROOT / "src" / "Typhon.Profiler"

LINE_RE = re.compile(r'^(?P<indent>[ \t]+)var size = ComputeSize\w*\([^)]*\);\s*$')
HAS_TC_RE = re.compile(r'^(?P<indent>[ \t]+)var has(TC|TraceContext) = traceIdHi != 0 \|\| traceIdLo != 0;\s*$')
FIX_LINE = "if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;"
HAS_SRC_LINE = "var hasSourceLocation = sourceLocationId != 0;"


def find_method_start(lines, idx):
    """Walk backward from idx to find the line that opens the enclosing method body
    (line ending in '{' that follows a method declaration containing 'sourceLocationId =')."""
    brace_depth = 0
    i = idx
    while i >= 0:
        s = lines[i].rstrip()
        # Track braces from the assignment side
        if s.endswith("}"):
            brace_depth += 1
        if s.endswith("{"):
            if brace_depth == 0:
                return i
            brace_depth -= 1
        i -= 1
    return -1


def method_takes_source_loc(lines, brace_idx):
    """Look at the lines preceding the opening brace at brace_idx; if any of them
    contain 'ushort sourceLocationId', this method takes the param."""
    i = brace_idx
    # Walk back collecting until we hit a blank line or another '{' or '}'
    while i >= 0:
        s = lines[i].rstrip()
        if "ushort sourceLocationId" in s:
            return True
        # Stop at signature top: typical header words
        if s.startswith("    internal static") or s.startswith("    public static") or s.startswith("    private static"):
            # also check this line for the param
            return "ushort sourceLocationId" in s
        if i < brace_idx - 30:
            return False
        i -= 1
    return False


def has_src_decl_in_method(lines, brace_idx, target_idx):
    """Check whether 'var hasSourceLocation = sourceLocationId != 0' appears between brace_idx and target_idx."""
    for i in range(brace_idx + 1, target_idx):
        if HAS_SRC_LINE in lines[i]:
            return True
    return False


def fix_file(path: Path) -> int:
    text = path.read_text(encoding="utf-8")
    lines = text.splitlines(keepends=True)
    inserted = 0
    i = 0
    while i < len(lines):
        line = lines[i]
        m = LINE_RE.match(line)
        if not m:
            i += 1
            continue

        indent = m.group("indent")
        # Already followed by the fix?
        j = i + 1
        while j < len(lines) and lines[j].strip() == "":
            j += 1
        if j < len(lines) and lines[j].strip() == FIX_LINE:
            i += 1
            continue

        # Find enclosing method open-brace
        brace_idx = find_method_start(lines, i - 1)
        if brace_idx < 0:
            print(f"  SKIP {path.name}:{i+1} — could not locate method open brace")
            i += 1
            continue
        if not method_takes_source_loc(lines, brace_idx):
            i += 1
            continue

        # Inject fix line after var size
        lines.insert(i + 1, f"{indent}{FIX_LINE}\n")
        inserted += 1

        # Ensure hasSourceLocation declaration exists
        if not has_src_decl_in_method(lines, brace_idx, i):
            # Find a hasTC/hasTraceContext line to insert next to
            inserted_decl = False
            for k in range(brace_idx + 1, i):
                hm = HAS_TC_RE.match(lines[k])
                if hm:
                    lines.insert(k + 1, f"{hm.group('indent')}{HAS_SRC_LINE}\n")
                    inserted_decl = True
                    break
            if not inserted_decl:
                # Insert just before var size line (now i+2 since we already inserted fix)
                # Actually i is still the var size line index since we inserted *after* it
                lines.insert(i, f"{indent}{HAS_SRC_LINE}\n")

        i = j + 2  # skip past inserted lines
    if inserted:
        path.write_text("".join(lines), encoding="utf-8")
    return inserted


def main() -> int:
    total = 0
    for cs in sorted(PROFILER_DIR.glob("*.cs")):
        n = fix_file(cs)
        if n:
            print(f"  {cs.relative_to(ROOT)}: {n} method(s) fixed")
            total += n
    print(f"\nTotal: {total} codec methods fixed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
