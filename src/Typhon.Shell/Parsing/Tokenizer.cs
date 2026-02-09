using System.Collections.Generic;
using System.Text;

namespace Typhon.Shell.Parsing;

/// <summary>
/// Lexical analyzer for shell input. Produces a list of tokens from a raw input string.
/// </summary>
internal static class Tokenizer
{
    public static List<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        var i = 0;

        while (i < input.Length)
        {
            // Skip whitespace
            if (char.IsWhiteSpace(input[i]))
            {
                i++;
                continue;
            }

            var start = i;

            switch (input[i])
            {
                case '{':
                    tokens.Add(new Token(TokenKind.OpenBrace, "{", start));
                    i++;
                    break;
                case '}':
                    tokens.Add(new Token(TokenKind.CloseBrace, "}", start));
                    i++;
                    break;
                case '(':
                    tokens.Add(new Token(TokenKind.OpenParen, "(", start));
                    i++;
                    break;
                case ')':
                    tokens.Add(new Token(TokenKind.CloseParen, ")", start));
                    i++;
                    break;
                case '=':
                    tokens.Add(new Token(TokenKind.Equals, "=", start));
                    i++;
                    break;
                case ',':
                    tokens.Add(new Token(TokenKind.Comma, ",", start));
                    i++;
                    break;
                case '#':
                    tokens.Add(new Token(TokenKind.Hash, "#", start));
                    i++;
                    break;
                case '@':
                    tokens.Add(new Token(TokenKind.At, "@", start));
                    i++;
                    break;
                case '-' when i + 1 < input.Length && input[i + 1] == '-':
                    tokens.Add(new Token(TokenKind.DoubleDash, "--", start));
                    i += 2;
                    break;
                case '"':
                    tokens.Add(ReadString(input, ref i));
                    break;
                case '\'':
                    tokens.Add(ReadChar(input, ref i));
                    break;
                default:
                    if (input[i] == '-' && i + 1 < input.Length && char.IsDigit(input[i + 1]))
                    {
                        tokens.Add(ReadNumber(input, ref i));
                    }
                    else if (char.IsDigit(input[i]))
                    {
                        tokens.Add(ReadNumber(input, ref i));
                    }
                    else if (char.IsLetter(input[i]) || input[i] == '_')
                    {
                        tokens.Add(ReadIdentifierOrKeyword(input, ref i));
                    }
                    else
                    {
                        // Unknown character — skip it
                        i++;
                    }
                    break;
            }
        }

        tokens.Add(new Token(TokenKind.End, "", input.Length));
        return tokens;
    }

    private static Token ReadString(string input, ref int i)
    {
        var start = i;
        i++; // skip opening "
        var sb = new StringBuilder();

        while (i < input.Length && input[i] != '"')
        {
            if (input[i] == '\\' && i + 1 < input.Length)
            {
                i++;
                sb.Append(input[i] switch
                {
                    'n'  => '\n',
                    'r'  => '\r',
                    't'  => '\t',
                    '\\' => '\\',
                    '"'  => '"',
                    '/'  => '/',
                    'b'  => '\b',
                    'f'  => '\f',
                    _    => input[i]
                });
                i++;
            }
            else
            {
                sb.Append(input[i]);
                i++;
            }
        }

        if (i < input.Length)
        {
            i++; // skip closing "
        }

        return new Token(TokenKind.String, sb.ToString(), start);
    }

    private static Token ReadChar(string input, ref int i)
    {
        var start = i;
        i++; // skip opening '

        var ch = i < input.Length ? input[i].ToString() : "";
        if (i < input.Length)
        {
            i++;
        }

        if (i < input.Length && input[i] == '\'')
        {
            i++; // skip closing '
        }

        return new Token(TokenKind.Char, ch, start);
    }

    private static Token ReadNumber(string input, ref int i)
    {
        var start = i;
        var isFloat = false;

        if (input[i] == '-')
        {
            i++;
        }

        while (i < input.Length && char.IsDigit(input[i]))
        {
            i++;
        }

        if (i < input.Length && input[i] == '.' && i + 1 < input.Length && char.IsDigit(input[i + 1]))
        {
            isFloat = true;
            i++; // skip dot
            while (i < input.Length && char.IsDigit(input[i]))
            {
                i++;
            }
        }

        // Check for number suffixes: uL, u, L, d, f
        if (i < input.Length)
        {
            if ((input[i] == 'u' || input[i] == 'U') && i + 1 < input.Length && (input[i + 1] == 'L' || input[i + 1] == 'l'))
            {
                i += 2;
            }
            else if (input[i] == 'u' || input[i] == 'U')
            {
                i++;
            }
            else if (input[i] == 'L' || input[i] == 'l')
            {
                i++;
            }
            else if (input[i] == 'd' || input[i] == 'D')
            {
                isFloat = true;
                i++;
            }
            else if (input[i] == 'f' || input[i] == 'F')
            {
                isFloat = true;
                i++;
            }
        }

        var text = input[start..i];
        return new Token(isFloat ? TokenKind.Float : TokenKind.Integer, text, start);
    }

    private static Token ReadIdentifierOrKeyword(string input, ref int i)
    {
        var start = i;
        while (i < input.Length && (char.IsLetterOrDigit(input[i]) || input[i] == '_' || input[i] == '-' || input[i] == '.'))
        {
            i++;
        }

        var text = input[start..i];
        return text.ToLowerInvariant() switch
        {
            "true" or "false" => new Token(TokenKind.Bool, text, start),
            _ => new Token(TokenKind.Identifier, text, start)
        };
    }
}
