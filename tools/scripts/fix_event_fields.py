"""
Pass 2b-fix: ensure EVERY ref struct in src/Typhon.Engine/Profiler/Events has the
SourceLocationId field. Earlier inject_field regex missed structs with blocky-form
ComputeSize / EncodeTo bodies (their internal `{}` confused the struct-body matcher).

Approach: line-based global replacement. After every `    public ulong TraceIdLo;`
line, ensure the next non-blank line is `    internal ushort SourceLocationId;`.
Idempotent.
"""
from pathlib import Path

EVENTS_DIR = Path("src/Typhon.Engine/Profiler/Events")

FIELD_DOC = "    /// <summary>Compile-time site id from <c>SourceLocationGenerator</c> (0 = not attributed). Wire-format implementation detail.</summary>"
FIELD = "    internal ushort SourceLocationId;"


def main():
    files_touched = 0
    inserts = 0
    for path in sorted(EVENTS_DIR.glob("*.cs")):
        if path.name == "ITraceEventEncoder.cs":
            continue
        text = path.read_text(encoding="utf-8")
        lines = text.split("\n")
        out = []
        i = 0
        n = len(lines)
        while i < n:
            line = lines[i]
            out.append(line)
            if line.rstrip() == "    public ulong TraceIdLo;":
                # Look ahead — is the next field already SourceLocationId?
                j = i + 1
                # Skip blank lines and the existing summary/comment
                while j < n and (lines[j].strip() == "" or lines[j].lstrip().startswith("///")):
                    j += 1
                if j < n and lines[j].rstrip() == FIELD:
                    # Already injected
                    pass
                else:
                    out.append(FIELD_DOC)
                    out.append(FIELD)
                    inserts += 1
            i += 1
        new_text = "\n".join(out)
        if new_text != text:
            path.write_text(new_text, encoding="utf-8")
            files_touched += 1
    print(f"fix_event_fields: {files_touched} files updated, {inserts} fields inserted.")


if __name__ == "__main__":
    main()
