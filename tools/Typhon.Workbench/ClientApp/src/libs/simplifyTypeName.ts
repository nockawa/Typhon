/**
 * Render a CLR assembly-qualified type name as a concise, developer-friendly form.
 *
 * Strips namespaces from every type in the tree, converts `Name`N[[...]]` (the CLR wire format for
 * generics) into `Name<...>`, and drops the trailing `, Assembly, Version, Culture, PublicKeyToken`
 * suffix from every assembly-qualified reference. Handles nesting and array rank suffixes.
 *
 * Examples:
 *  - `System.Int32`                                                      → `Int32`
 *  - `System.Byte[]`                                                     → `Byte[]`
 *  - `Typhon.Schema.Definition.ComponentCollection``1[[Typhon.Schema.Definition.String64, Typhon.Schema.Definition, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]]`
 *    → `ComponentCollection<String64>`
 */
export function simplifyTypeName(input: string): string {
  if (input.length === 0) return input;
  return parseType(input, 0).text;
}

interface ParseResult {
  text: string;
  end: number;
}

function parseType(s: string, start: number): ParseResult {
  let i = start;
  const nameStart = i;

  // Scan the base name. Stop at separators that indicate "end of this type's name segment": a
  // backtick (generic arity marker), a comma (end of a type reference in an assembly-qualified
  // argument list), or a closing bracket (end of a generic-arg block).
  // `[` is *allowed* inside the name only when it's an array-rank suffix like `[]` or `[,]`.
  while (i < s.length) {
    const c = s[i];
    if (c === '`' || c === ',' || c === ']') break;
    if (c === '[') {
      const closed = matchArrayRankClose(s, i);
      if (closed === -1) break; // not an array suffix — exit name scan
      i = closed + 1;
      continue;
    }
    i++;
  }
  const fullName = s.slice(nameStart, i).trim();

  // Split any trailing array suffix off the name so we can strip the namespace cleanly, then
  // re-append the suffix to the simplified form.
  let baseName: string;
  let arraySuffix: string;
  const arr = /^(.+?)((?:\[[,\s]*\])+)$/.exec(fullName);
  if (arr) {
    baseName = stripNamespace(arr[1]);
    arraySuffix = arr[2];
  } else {
    baseName = stripNamespace(fullName);
    arraySuffix = '';
  }

  // Generic arity + arguments. CLR writes `Name``N[[arg, asm, ver, ...], [arg2, ...]]`. A `[` right
  // after the digits means we have an argument list to consume.
  if (s[i] === '`') {
    i++; // skip '`'
    while (i < s.length && s[i] >= '0' && s[i] <= '9') i++;
    if (s[i] === '[') {
      const doubleOpen = s[i + 1] === '[';
      i += doubleOpen ? 2 : 1;
      const args: string[] = [];
      for (;;) {
        const arg = parseType(s, i);
        args.push(arg.text);
        i = arg.end;
        // If we're inside a `[[ .. ]]` pair, each arg is further wrapped with its own assembly
        // info: "name, Assembly, Version=..., ...]". Skip past that to the inner `]`.
        if (doubleOpen) {
          let depth = 0;
          while (i < s.length) {
            const c = s[i];
            if (c === '[') depth++;
            else if (c === ']') {
              if (depth === 0) break;
              depth--;
            }
            i++;
          }
          if (s[i] === ']') i++;
        }
        // Separator between args, or end of the list.
        if (s[i] === ',') {
          i++;
          while (s[i] === ' ') i++;
          if (doubleOpen && s[i] === '[') i++;
          continue;
        }
        if (s[i] === ']') {
          i++;
          break;
        }
        break;
      }
      baseName = `${baseName}<${args.join(', ')}>`;
    }
  }

  // Post-generic array rank: handles cases like `List<int>[]` whose FullName is
  // `...List``1[[System.Int32, ...]][]` — the `[]` lives after the closing generic bracket.
  while (s[i] === '[') {
    const closed = matchArrayRankClose(s, i);
    if (closed === -1) break;
    arraySuffix += s.slice(i, closed + 1);
    i = closed + 1;
  }

  return { text: baseName + arraySuffix, end: i };
}

// If `s[i] === '['` and the bracket encloses only commas and whitespace (i.e., it's an array rank
// `[]` or `[,]`), return the index of the matching `]`. Otherwise -1.
function matchArrayRankClose(s: string, open: number): number {
  let j = open + 1;
  while (j < s.length && (s[j] === ',' || s[j] === ' ')) j++;
  return s[j] === ']' ? j : -1;
}

function stripNamespace(full: string): string {
  const i = full.lastIndexOf('.');
  return i === -1 ? full : full.slice(i + 1);
}
