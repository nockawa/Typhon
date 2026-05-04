"""
One-shot mechanical conversion script for Phase 1 of the Profiler Source Attribution feature.
Convert each `BeginXxx(args)` factory in TyphonEvent.cs into:
    - Pass-through: `BeginXxx(args) => BeginXxxWithSiteId(0, args);`
    - New WithSiteId factory carrying the original body + setting SourceLocationId.

Skips the already-converted BeginBTreeInsert.

Run from repo root:
    python3 tools/scripts/convert_typhon_factories.py

Idempotent: factories that already have a WithSiteId sibling are skipped.
"""
import re
import sys
from pathlib import Path

SRC = Path("src/Typhon.Engine/Profiler/TyphonEvent.cs")

def main():
    text = SRC.read_text(encoding="utf-8")
    lines = text.split("\n")

    # Track factories already converted (have a `BeginXxxWithSiteId` sibling).
    converted = set()
    for m in re.finditer(r"public static \w+Event (Begin\w+)WithSiteId\(", text):
        converted.add(m.group(1))

    out = []
    i = 0
    n = len(lines)
    factories_converted = 0
    factories_skipped = 0

    while i < n:
        line = lines[i]

        # Match the [MethodImpl(...)] line followed by `public static <RT> Begin<Name>(<params>)` —
        # the canonical factory shape.
        if line.strip() == "[MethodImpl(MethodImplOptions.AggressiveInlining)]" and i + 1 < n:
            sig_line = lines[i + 1]
            sig_match = re.match(
                r"^(\s*)public static (\w+Event) (Begin\w+)\(([^)]*)\)\s*$",
                sig_line,
            )
            if sig_match:
                indent, return_type, name, params_str = sig_match.groups()

                # Skip already-converted factories (their WithSiteId sibling exists) and the
                # WithSiteId variants themselves.
                if name.endswith("WithSiteId") or name in converted:
                    out.append(line)
                    i += 1
                    continue

                # Find the function body: starts at next "{", ends at matching "}".
                # Track brace depth.
                brace_start = i + 2
                while brace_start < n and lines[brace_start].strip() != "{":
                    brace_start += 1
                if brace_start >= n:
                    out.append(line)
                    i += 1
                    continue

                # Walk to the matching close brace.
                depth = 0
                brace_end = brace_start
                for j in range(brace_start, n):
                    for c in lines[j]:
                        if c == "{":
                            depth += 1
                        elif c == "}":
                            depth -= 1
                            if depth == 0:
                                brace_end = j
                                break
                    if depth == 0:
                        break

                # Extract the body (lines between brace_start and brace_end inclusive).
                body_lines = lines[brace_start : brace_end + 1]

                # Build the pass-through one-liner version of the original.
                # If params_str is empty: `=> BeginXxxWithSiteId(0);`
                # Otherwise: pass siteId=0 + the original arg names.
                # Argument names: extract the trailing identifier from each `Type name` pair.
                arg_names = []
                if params_str.strip():
                    for p in params_str.split(","):
                        p = p.strip()
                        # Last whitespace-separated token is the name.
                        arg_names.append(p.rsplit(None, 1)[-1])

                forward_args = ", ".join(["0"] + arg_names) if arg_names else "0"

                # Pass-through replaces the original brace-bounded body.
                passthrough_line = f"{indent}public static {return_type} {name}({params_str}) => {name}WithSiteId({forward_args});"

                # New WithSiteId variant body — original body with SourceLocationId = siteId injected.
                new_params = "ushort siteId" + (", " + params_str if params_str.strip() else "")
                new_sig_line = f"{indent}public static {return_type} {name}WithSiteId({new_params})"

                # Inject `SourceLocationId = siteId,` into the `return new <RT> { ... }` initializer.
                # Find the line containing `return new ` in body_lines, then find the `}` that closes it.
                injected_body = inject_source_location_id(body_lines)

                # Now emit:
                #   <original [MethodImpl] line>           ← already in 'line'
                #   <pass-through one-liner>
                #   blank line
                #   <[MethodImpl] line>
                #   <new WithSiteId signature>
                #   <body (with SourceLocationId injected)>

                out.append(line)                       # [MethodImpl(...)] for the pass-through
                out.append(passthrough_line)
                out.append("")
                out.append(line)                       # [MethodImpl(...)] for the WithSiteId variant
                out.append(new_sig_line)
                out.extend(injected_body)

                factories_converted += 1
                i = brace_end + 1
                continue

        out.append(line)
        i += 1

    SRC.write_text("\n".join(out), encoding="utf-8")
    print(f"converted {factories_converted} factories, {factories_skipped} skipped (already had WithSiteId)")


def inject_source_location_id(body_lines):
    """
    Walk body_lines, find the `return new <Event> {` block, find its closing `};`,
    and inject `            SourceLocationId = siteId,` just before the close.
    """
    out = []
    in_init = False
    init_close_seen = False
    init_indent = "        "  # canonical body field indent

    for line in body_lines:
        stripped = line.strip()
        if not in_init and stripped.startswith("return new ") and stripped.endswith("{"):
            in_init = True
            out.append(line)
            continue

        if in_init and not init_close_seen and (stripped == "};" or stripped.startswith("};")):
            # Inject just before the close.
            # Use the same indent as the field lines above (the first prior line that's a field).
            field_indent = init_indent
            for prev in reversed(out):
                m = re.match(r"^(\s+)\w", prev)
                if m and prev.strip().endswith(","):
                    field_indent = m.group(1)
                    break
            out.append(f"{field_indent}SourceLocationId = siteId,")
            out.append(line)
            init_close_seen = True
            in_init = False
            continue

        out.append(line)

    return out


if __name__ == "__main__":
    main()
