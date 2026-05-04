"""
Pass 3-fix: remove `if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;`
injections that landed in methods where `hasSourceLocation` isn't declared (Compute helpers,
Decode methods, etc.). Walks each method scope and removes orphan injections.

Method-aware logic: scan for top-level method bodies, check if `sourceLocationId` is in the
signature OR `hasSourceLocation = ` is declared. If neither, strip any injected size adjustments
inside that method.
"""
import re
from pathlib import Path

CODECS = list(Path("src/Typhon.Profiler").glob("*EventCodec.cs"))


def main():
    files_touched = 0
    for path in sorted(CODECS):
        text = path.read_text(encoding="utf-8")
        original = text
        text = scrub_orphan_lines(text)
        if text != original:
            path.write_text(text, encoding="utf-8")
            files_touched += 1
    print(f"fix_orphan_injections: {files_touched} files cleaned.")


def scrub_orphan_lines(text: str) -> str:
    """
    Walk the file, tracking each top-level method scope. Inside each method, if neither
    `ushort sourceLocationId` (param) nor `var hasSourceLocation` (local) appears, strip
    `if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;` lines.
    """
    lines = text.split("\n")
    out = []
    i = 0
    n = len(lines)

    while i < n:
        line = lines[i]
        # Detect method boundary: a line like `    internal static ... ` or `    public static ...`
        # at 4-space indent that ends in a parameter list or open brace.
        m = re.match(r"^    (?:internal|public|private)\s+static\s+\w[\w<>?, ]*\s+\w+\s*\(", line)
        if m:
            # Capture the entire method: header + body.
            # Read until the matching `}` at column 4.
            method_start = i
            # Scan forward to the body's open brace.
            j = i
            while j < n and lines[j].rstrip() != "    {":
                j += 1
            if j >= n:
                out.append(line)
                i += 1
                continue
            body_start = j
            # Find matching close brace at column 4.
            depth = 1
            k = body_start + 1
            while k < n and depth > 0:
                stripped = lines[k]
                # Track depth by counting braces but only at column 4 (top-level method braces).
                # Simpler: track all braces.
                for c in stripped:
                    if c == "{":
                        depth += 1
                    elif c == "}":
                        depth -= 1
                        if depth == 0:
                            break
                if depth == 0:
                    break
                k += 1
            method_end = k

            # Examine the method.
            method_text = "\n".join(lines[method_start:method_end + 1])
            has_param = "ushort sourceLocationId" in method_text
            has_decl = "var hasSourceLocation" in method_text

            if has_param or has_decl:
                # Method already supports source location; emit unchanged.
                out.extend(lines[method_start:method_end + 1])
            else:
                # Strip orphan injections.
                for ml in lines[method_start:method_end + 1]:
                    if ml.strip() == "if (hasSourceLocation) size += TraceRecordHeader.SourceLocationIdSize;":
                        continue
                    out.append(ml)
            i = method_end + 1
            continue

        out.append(line)
        i += 1

    return "\n".join(out)


if __name__ == "__main__":
    main()
