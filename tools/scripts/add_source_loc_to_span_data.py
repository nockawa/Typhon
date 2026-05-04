"""For *Data readonly structs that are span-shaped (have both StartTimestamp and DurationTicks
properties) but DO NOT expose TraceIdHi/TraceIdLo, add SourceLocationId support.

This complements add_source_loc_to_eventdata.py which only handles structs that DO expose
trace context. These 'private trace ctx' span structs need source-loc too.

Pattern applied:
  + public ushort SourceLocationId { get; }
  + public bool HasSourceLocation => SourceLocationId != 0;
  + ctor takes 'ushort srcLoc = 0' as final param
  + ctor assigns: SourceLocationId = srcLoc;

Idempotent: skips structs already having SourceLocationId.
"""

import re
import sys
from pathlib import Path

if len(sys.argv) < 2:
    print("usage: add_source_loc_to_span_data.py <file.cs>")
    sys.exit(1)

FILE = Path(sys.argv[1])
text = FILE.read_text(encoding="utf-8")

STRUCT_RE = re.compile(
    r'(public readonly struct (?P<name>\w+Data)\s*\n\{\n)(?P<body>.*?)(\n\})',
    re.DOTALL,
)


def is_span_no_tc(body: str) -> bool:
    return ("StartTimestamp" in body and "DurationTicks" in body
            and "TraceIdHi" not in body and "SourceLocationId" not in body)


def add_field_and_ctor(body: str) -> str:
    field = "    public ushort SourceLocationId { get; }\n"
    has_src_prop = "    public bool HasSourceLocation => SourceLocationId != 0;\n"
    # Insert before public ctor line
    m = re.search(r'^(?P<indent>[ \t]+)public \w+Data\(', body, re.MULTILINE)
    if not m:
        return body
    body = body[:m.start()] + field + has_src_prop + body[m.start():]

    # Find ctor signature: "public XxxData(args)"
    ctor_re = re.compile(r'public (\w+Data)\(([^)]*)\)', re.DOTALL)
    m2 = ctor_re.search(body)
    if not m2:
        return body
    args = m2.group(2).strip()
    if "ushort srcLoc" in args:
        return body
    new_args = args + ", ushort srcLoc = 0"
    body = body[:m2.start(2)] + new_args + body[m2.end(2):]

    # Add 'SourceLocationId = srcLoc;' before final '}' of ctor body
    open_brace = body.index("{", m2.end())
    depth = 0
    close_brace_idx = None
    for i in range(open_brace, len(body)):
        c = body[i]
        if c == "{": depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                close_brace_idx = i
                break
    if close_brace_idx is None:
        return body
    inner = body[open_brace + 1: close_brace_idx]
    if "SourceLocationId" not in inner:
        new_inner = inner.rstrip()
        if not new_inner.endswith(";"):
            new_inner += ";"
        new_inner += " SourceLocationId = srcLoc;"
        body = body[:open_brace + 1] + " " + new_inner + " " + body[close_brace_idx:]
    return body


def process(text: str) -> tuple[str, int]:
    count = 0
    out = []
    cursor = 0
    for m in STRUCT_RE.finditer(text):
        out.append(text[cursor:m.start()])
        body = m.group("body")
        if not is_span_no_tc(body):
            out.append(m.group(0))
            cursor = m.end()
            continue
        new_body = add_field_and_ctor(body)
        out.append(m.group(1) + new_body + m.group(4))
        count += 1
        cursor = m.end()
    out.append(text[cursor:])
    return "".join(out), count


new_text, n = process(text)
if n:
    FILE.write_text(new_text, encoding="utf-8")
print(f"{FILE.name}: {n} struct(s) updated")
