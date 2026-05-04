"""
For every method with `ushort sourceLocationId` parameter, ensure the body's first executable
line is `var hasSourceLocation = sourceLocationId != 0;`. This guarantees the variable is in
scope wherever the size-adjustment script injected `if (hasSourceLocation) size += ...;`.

Idempotent.
"""
import re
from pathlib import Path

CODECS = list(Path("src/Typhon.Profiler").glob("*EventCodec.cs"))


def main():
    files_touched = 0
    methods_fixed = 0
    for path in sorted(CODECS):
        text = path.read_text(encoding="utf-8")
        original = text
        text, n = inject_decl(text)
        if text != original:
            path.write_text(text, encoding="utf-8")
            files_touched += 1
            methods_fixed += n
    print(f"ensure_hasSourceLocation_decl: {files_touched} files, {methods_fixed} methods got the local decl.")


def inject_decl(text: str) -> tuple[str, int]:
    """
    Strategy: find each `        ushort sourceLocationId = 0)` line (closing paren of a method
    signature with the new param). The next `        {` line is the body's open brace. Inject
    the decl on the very next line if not already present.
    """
    lines = text.split("\n")
    out = []
    i = 0
    n = len(lines)
    count = 0
    while i < n:
        line = lines[i]
        out.append(line)
        # Match a line like `        ushort sourceLocationId = 0)` (with various leading whitespace).
        if re.match(r"^\s*ushort sourceLocationId = 0\)\s*$", line):
            # Walk forward to the body's opening brace `{`.
            j = i + 1
            while j < n and lines[j].strip() != "{":
                out.append(lines[j])
                j += 1
            if j < n:
                out.append(lines[j])  # the `{`
                # Look at the next non-blank line. If it isn't already the decl, inject it.
                k = j + 1
                while k < n and lines[k].strip() == "":
                    out.append(lines[k])
                    k += 1
                # Determine indent: same as the next line's, fallback to 8 spaces.
                indent = "        "
                if k < n:
                    m = re.match(r"^(\s+)\S", lines[k])
                    if m:
                        indent = m.group(1)
                injected = f"{indent}var hasSourceLocation = sourceLocationId != 0;"
                if k < n and lines[k].strip() == "var hasSourceLocation = sourceLocationId != 0;":
                    pass  # already there
                else:
                    out.append(injected)
                    count += 1
                # Continue from k (the next non-blank line we saved).
                # We've already appended j and intermediate blanks; advance i past them.
                i = k
                continue
        i += 1
    return "\n".join(out), count


if __name__ == "__main__":
    main()
