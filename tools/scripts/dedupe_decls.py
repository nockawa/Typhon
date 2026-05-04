"""
Final scrub: remove duplicate `var hasSourceLocation = sourceLocationId != 0;` declarations
within the same method scope. Keeps the FIRST occurrence.
"""
from pathlib import Path

CODECS = list(Path("src/Typhon.Profiler").glob("*EventCodec.cs"))
TARGET = "var hasSourceLocation = sourceLocationId != 0;"


def main():
    files_touched = 0
    removals = 0
    for path in sorted(CODECS):
        text = path.read_text(encoding="utf-8")
        original = text
        text, n = scrub(text)
        if text != original:
            path.write_text(text, encoding="utf-8")
            files_touched += 1
            removals += n
    print(f"dedupe_decls: {files_touched} files, removed {removals} duplicate decls.")


def scrub(text: str) -> tuple[str, int]:
    """
    Walk lines; track method-body brace depth; reset 'seen' flag at each method boundary.
    """
    lines = text.split("\n")
    out = []
    seen_in_method = False
    depth = 0
    removed = 0
    for line in lines:
        stripped = line.strip()
        # Track brace depth so we can reset seen_in_method when a method ends.
        # We treat depth==0 as outside any method.
        if depth == 0:
            seen_in_method = False
        if stripped == TARGET:
            if seen_in_method:
                removed += 1
                continue
            seen_in_method = True
        out.append(line)
        # Update depth.
        for c in line:
            if c == "{":
                depth += 1
            elif c == "}":
                depth -= 1
                if depth <= 0:
                    depth = 0
                    seen_in_method = False
    return "\n".join(out), removed


if __name__ == "__main__":
    main()
