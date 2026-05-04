"""Adds SourceLocationId getter + ctor param + assignment to each *Data readonly struct
in a codec file. Idempotent: skips structs that already have SourceLocationId.

Pattern applied to each struct:
  + public ushort SourceLocationId { get; }
  + public bool HasSourceLocation => SourceLocationId != 0;
  + ctor takes 'ushort srcLoc = 0' as final param
  + ctor assigns: SourceLocationId = srcLoc;

Strategy:
  - Find struct blocks whose first ctor parameter list ends in something
    like (..., ulong tlo, ...): heuristically all profiler *Data span structs.
  - Skip if struct already has SourceLocationId.
  - We DO NOT touch instant-only structs (no SpanId) — those have no source-loc.
"""

import re
import sys
from pathlib import Path

if len(sys.argv) < 2:
    print("usage: add_source_loc_to_eventdata.py <file.cs>")
    sys.exit(1)

FILE = Path(sys.argv[1])
text = FILE.read_text(encoding="utf-8")

STRUCT_RE = re.compile(
    r'(public readonly struct (?P<name>\w+Data)\s*\n\{\n)(?P<body>.*?)(\n\})',
    re.DOTALL,
)

CTOR_LINE_RE = re.compile(r'^(?P<indent>[ \t]*)public (?P<name>\w+Data)\((?P<args>[^)]*)\)\s*$', re.MULTILINE)
CTOR_BODY_RE = re.compile(
    r'(?P<head>public (?P<cname>\w+Data)\((?P<args>[^)]*)\)\s*\n\s*\{[^}]*?)(?P<tail>\})',
    re.DOTALL,
)


def already_has_source_loc(body: str) -> bool:
    return "SourceLocationId" in body


def add_field(body: str, indent: str = "    ") -> str:
    # Insert SourceLocationId getter before the HasTraceContext line if present, else before ctor
    field = f"{indent}public ushort SourceLocationId {{ get; }}\n"
    has_src_prop = f"{indent}public bool HasSourceLocation => SourceLocationId != 0;\n"
    # Look for HasTraceContext line and insert before it
    m = re.search(r'^(?P<indent>[ \t]+)public bool HasTraceContext => .*?$', body, re.MULTILINE)
    if m:
        return body[:m.start()] + field + has_src_prop + body[m.start():]
    # Fallback: insert before the public ctor
    m = re.search(r'^(?P<indent>[ \t]+)public \w+Data\(', body, re.MULTILINE)
    if m:
        return body[:m.start()] + field + has_src_prop + "\n" + body[m.start():]
    return body


def patch_ctor(body: str) -> str:
    # Find ctor: "public XxxData(args)" + body block on next lines
    m = CTOR_LINE_RE.search(body)
    if not m:
        return body
    args = m.group("args").strip()
    if "ushort srcLoc" in args:
        return body
    new_args = args + ", ushort srcLoc = 0"
    body = body[:m.start("args")] + new_args + body[m.end("args"):]
    # Find ctor body (between { and }) following the signature line
    after = m.end()
    open_brace = body.index("{", after)
    close_brace_idx = open_brace
    depth = 0
    for i in range(open_brace, len(body)):
        c = body[i]
        if c == "{": depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                close_brace_idx = i
                break
    # Insert assignment before final }
    inner = body[open_brace + 1: close_brace_idx]
    if "SourceLocationId" not in inner:
        # Determine indent of last assignment
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
        full = m.group(0)
        body = m.group("body")
        # Skip non-span structs (no SpanId / no TraceIdHi means it's not a span data struct)
        if "SpanId" not in body or "TraceIdHi" not in body:
            out.append(full)
            cursor = m.end()
            continue
        if already_has_source_loc(body):
            out.append(full)
            cursor = m.end()
            continue
        new_body = add_field(body)
        new_body = patch_ctor(new_body)
        out.append(m.group(1) + new_body + m.group(4))
        count += 1
        cursor = m.end()
    out.append(text[cursor:])
    return "".join(out), count


new_text, count = process(text)
if count:
    FILE.write_text(new_text, encoding="utf-8")
print(f"{FILE.name}: {count} struct(s) updated")
