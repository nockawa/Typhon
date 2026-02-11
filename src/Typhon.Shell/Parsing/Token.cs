namespace Typhon.Shell.Parsing;

internal enum TokenKind
{
    Identifier,     // command names, component names, field names
    Integer,        // 42, -5
    Float,          // 95.5, 3.14d
    String,         // "hello"
    Char,           // 'A'
    Bool,           // true, false
    Hash,           // #
    OpenBrace,      // {
    CloseBrace,     // }
    OpenParen,      // (
    CloseParen,     // )
    Equals,         // =
    Comma,          // ,
    DoubleDash,     // --
    At,             // @
    End,            // end of input
}

internal readonly struct Token
{
    public TokenKind Kind { get; }
    public string Value { get; }
    public int Position { get; }

    public Token(TokenKind kind, string value, int position)
    {
        Kind = kind;
        Value = value;
        Position = position;
    }

    public override string ToString() => $"{Kind}({Value})@{Position}";
}
