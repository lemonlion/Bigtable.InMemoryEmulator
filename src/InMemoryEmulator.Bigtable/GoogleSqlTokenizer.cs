using Superpower;
using Superpower.Display;
using Superpower.Model;
using Superpower.Parsers;
using Superpower.Tokenizers;

namespace InMemoryEmulator.Bigtable;

/// <summary>
/// Token types for the GoogleSQL subset supported by Bigtable's ExecuteQuery.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#executequeryrequestusing
/// </summary>
internal enum GoogleSqlToken
{
    // Literals
    [Token(Example = "123")]
    IntegerLiteral,

    [Token(Example = "1.5")]
    FloatLiteral,

    [Token(Example = "'hello'")]
    StringLiteral,

    [Token(Example = "b'bytes'")]
    BytesLiteral,

    // Identifiers and keywords
    [Token(Example = "name")]
    Identifier,

    [Token(Example = "@param")]
    Parameter,

    // Keywords
    [Token(Example = "SELECT")]
    Select,
    [Token(Example = "FROM")]
    From,
    [Token(Example = "WHERE")]
    Where,
    [Token(Example = "ORDER")]
    Order,
    [Token(Example = "BY")]
    By,
    [Token(Example = "GROUP")]
    Group,
    [Token(Example = "HAVING")]
    Having,
    [Token(Example = "LIMIT")]
    Limit,
    [Token(Example = "OFFSET")]
    Offset,
    [Token(Example = "AS")]
    As,
    [Token(Example = "AND")]
    And,
    [Token(Example = "OR")]
    Or,
    [Token(Example = "NOT")]
    Not,
    [Token(Example = "IN")]
    In,
    [Token(Example = "BETWEEN")]
    Between,
    [Token(Example = "LIKE")]
    Like,
    [Token(Example = "IS")]
    Is,
    [Token(Example = "NULL")]
    Null,
    [Token(Example = "TRUE")]
    True,
    [Token(Example = "FALSE")]
    False,
    [Token(Example = "ASC")]
    Asc,
    [Token(Example = "DESC")]
    Desc,
    [Token(Example = "DISTINCT")]
    Distinct,
    [Token(Example = "CAST")]
    Cast,
    [Token(Example = "SAFE_CAST")]
    SafeCast,
    [Token(Example = "CASE")]
    Case,
    [Token(Example = "WHEN")]
    When,
    [Token(Example = "THEN")]
    Then,
    [Token(Example = "ELSE")]
    Else,
    [Token(Example = "END")]
    End,
    [Token(Example = "TOP")]
    Top,

    // Type keywords
    [Token(Example = "INT64")]
    TypeInt64,
    [Token(Example = "FLOAT64")]
    TypeFloat64,
    [Token(Example = "FLOAT32")]
    TypeFloat32,
    [Token(Example = "BOOL")]
    TypeBool,
    [Token(Example = "STRING")]
    TypeString,
    [Token(Example = "BYTES")]
    TypeBytes,
    [Token(Example = "TIMESTAMP")]
    TypeTimestamp,
    [Token(Example = "DATE")]
    TypeDate,
    [Token(Example = "ARRAY")]
    TypeArray,
    [Token(Example = "MAP")]
    TypeMap,
    [Token(Example = "STRUCT")]
    TypeStruct,

    // Operators
    [Token(Example = "+")]
    Plus,
    [Token(Example = "-")]
    Minus,
    [Token(Example = "*")]
    Star,
    [Token(Example = "/")]
    Slash,
    [Token(Example = "%")]
    Percent,
    [Token(Example = "=")]
    Equal,
    [Token(Example = "!=")]
    NotEqual,
    [Token(Example = "<>")]
    NotEqual2,
    [Token(Example = "<")]
    LessThan,
    [Token(Example = ">")]
    GreaterThan,
    [Token(Example = "<=")]
    LessOrEqual,
    [Token(Example = ">=")]
    GreaterOrEqual,

    // Pipe operator
    [Token(Example = "|>")]
    Pipe,

    // Window function keywords
    Over,
    Partition,

    // Delimiters
    [Token(Example = "(")]
    LeftParen,
    [Token(Example = ")")]
    RightParen,
    [Token(Example = "[")]
    LeftBracket,
    [Token(Example = "]")]
    RightBracket,
    [Token(Example = ",")]
    Comma,
    [Token(Example = ".")]
    Dot,
    [Token(Example = ";")]
    Semicolon,
}

/// <summary>
/// GoogleSQL tokenizer built with Superpower.
/// </summary>
internal static class GoogleSqlTokenizer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "ORDER", "BY", "GROUP", "HAVING",
        "LIMIT", "OFFSET", "AS", "AND", "OR", "NOT", "IN", "BETWEEN",
        "LIKE", "IS", "NULL", "TRUE", "FALSE", "ASC", "DESC", "DISTINCT",
        "CAST", "SAFE_CAST", "CASE", "WHEN", "THEN", "ELSE", "END", "TOP",
        "INT64", "FLOAT64", "FLOAT32", "BOOL", "STRING", "BYTES",
        "TIMESTAMP", "DATE", "ARRAY", "MAP", "STRUCT",
        "OVER", "PARTITION",
    };

    private static readonly Dictionary<string, GoogleSqlToken> KeywordTokens =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["SELECT"] = GoogleSqlToken.Select,
        ["FROM"] = GoogleSqlToken.From,
        ["WHERE"] = GoogleSqlToken.Where,
        ["ORDER"] = GoogleSqlToken.Order,
        ["BY"] = GoogleSqlToken.By,
        ["GROUP"] = GoogleSqlToken.Group,
        ["HAVING"] = GoogleSqlToken.Having,
        ["LIMIT"] = GoogleSqlToken.Limit,
        ["OFFSET"] = GoogleSqlToken.Offset,
        ["AS"] = GoogleSqlToken.As,
        ["AND"] = GoogleSqlToken.And,
        ["OR"] = GoogleSqlToken.Or,
        ["NOT"] = GoogleSqlToken.Not,
        ["IN"] = GoogleSqlToken.In,
        ["BETWEEN"] = GoogleSqlToken.Between,
        ["LIKE"] = GoogleSqlToken.Like,
        ["IS"] = GoogleSqlToken.Is,
        ["NULL"] = GoogleSqlToken.Null,
        ["TRUE"] = GoogleSqlToken.True,
        ["FALSE"] = GoogleSqlToken.False,
        ["ASC"] = GoogleSqlToken.Asc,
        ["DESC"] = GoogleSqlToken.Desc,
        ["DISTINCT"] = GoogleSqlToken.Distinct,
        ["CAST"] = GoogleSqlToken.Cast,
        ["SAFE_CAST"] = GoogleSqlToken.SafeCast,
        ["CASE"] = GoogleSqlToken.Case,
        ["WHEN"] = GoogleSqlToken.When,
        ["THEN"] = GoogleSqlToken.Then,
        ["ELSE"] = GoogleSqlToken.Else,
        ["END"] = GoogleSqlToken.End,
        ["TOP"] = GoogleSqlToken.Top,
        ["INT64"] = GoogleSqlToken.TypeInt64,
        ["FLOAT64"] = GoogleSqlToken.TypeFloat64,
        ["FLOAT32"] = GoogleSqlToken.TypeFloat32,
        ["BOOL"] = GoogleSqlToken.TypeBool,
        ["STRING"] = GoogleSqlToken.TypeString,
        ["BYTES"] = GoogleSqlToken.TypeBytes,
        ["TIMESTAMP"] = GoogleSqlToken.TypeTimestamp,
        ["DATE"] = GoogleSqlToken.TypeDate,
        ["ARRAY"] = GoogleSqlToken.TypeArray,
        ["MAP"] = GoogleSqlToken.TypeMap,
        ["STRUCT"] = GoogleSqlToken.TypeStruct,
        ["OVER"] = GoogleSqlToken.Over,
        ["PARTITION"] = GoogleSqlToken.Partition,
    };

    private static readonly TextParser<TextSpan> DoubleQuotedString =
        Character.EqualTo('"')
            .IgnoreThen(Span.MatchedBy(
                Character.Except('"').Or(Span.EqualTo("\"\"").Value('"')).Many()))
            .Then(s => Character.EqualTo('"').Value(s));

    public static readonly Tokenizer<GoogleSqlToken> Instance =
        new TokenizerBuilder<GoogleSqlToken>()
            // Multi-char operators (must be before single-char)
            .Match(Span.EqualTo("|>"), GoogleSqlToken.Pipe)
            .Match(Span.EqualTo("!="), GoogleSqlToken.NotEqual)
            .Match(Span.EqualTo("<>"), GoogleSqlToken.NotEqual2)
            .Match(Span.EqualTo("<="), GoogleSqlToken.LessOrEqual)
            .Match(Span.EqualTo(">="), GoogleSqlToken.GreaterOrEqual)

            // Single-char operators and delimiters
            .Match(Character.EqualTo('+'), GoogleSqlToken.Plus)
            .Match(Character.EqualTo('-'), GoogleSqlToken.Minus)
            .Match(Character.EqualTo('*'), GoogleSqlToken.Star)
            .Match(Character.EqualTo('/'), GoogleSqlToken.Slash)
            .Match(Character.EqualTo('%'), GoogleSqlToken.Percent)
            .Match(Character.EqualTo('='), GoogleSqlToken.Equal)
            .Match(Character.EqualTo('<'), GoogleSqlToken.LessThan)
            .Match(Character.EqualTo('>'), GoogleSqlToken.GreaterThan)
            .Match(Character.EqualTo('('), GoogleSqlToken.LeftParen)
            .Match(Character.EqualTo(')'), GoogleSqlToken.RightParen)
            .Match(Character.EqualTo('['), GoogleSqlToken.LeftBracket)
            .Match(Character.EqualTo(']'), GoogleSqlToken.RightBracket)
            .Match(Character.EqualTo(','), GoogleSqlToken.Comma)
            .Match(Character.EqualTo('.'), GoogleSqlToken.Dot)
            .Match(Character.EqualTo(';'), GoogleSqlToken.Semicolon)

            // Parameters (@name)
            .Match(Character.EqualTo('@').IgnoreThen(Span.MatchedBy(
                Character.LetterOrDigit.Or(Character.EqualTo('_')).AtLeastOnce())),
                GoogleSqlToken.Parameter, requireDelimiters: true)

            // Byte literals (b'...' or b"...")
            .Match(Span.MatchedBy(
                Character.EqualTo('b').Or(Character.EqualTo('B')).IgnoreThen(
                    Span.MatchedBy(QuotedString.SqlStyle)!
                        .Or(Span.MatchedBy(DoubleQuotedString!)!)))!,
                GoogleSqlToken.BytesLiteral, requireDelimiters: true)

            // String literals
            .Match(QuotedString.SqlStyle, GoogleSqlToken.StringLiteral)
            .Match(DoubleQuotedString!, GoogleSqlToken.StringLiteral)

            // Numbers (int before float — DecimalDouble also matches integers)
            .Match(Numerics.IntegerInt64, GoogleSqlToken.IntegerLiteral, requireDelimiters: true)
            .Match(Numerics.DecimalDouble, GoogleSqlToken.FloatLiteral, requireDelimiters: true)

            // Identifiers and keywords (backtick-quoted identifiers)
            .Match(Span.MatchedBy(
                Character.Letter.Or(Character.EqualTo('_'))
                    .IgnoreThen(Character.LetterOrDigit
                        .Or(Character.EqualTo('_')).Many())),
                GoogleSqlToken.Identifier, requireDelimiters: true)

            .Ignore(Span.WhiteSpace)
            .Build();

    /// <summary>
    /// Tokenizes SQL input and resolves keywords from identifiers.
    /// </summary>
    public static TokenList<GoogleSqlToken> Tokenize(string sql)
    {
        var result = Instance.Tokenize(sql);
        // Post-process: promote identifiers to keyword tokens
        var tokens = result.ToArray();
        var resolved = new List<Token<GoogleSqlToken>>(tokens.Length);

        foreach (var token in tokens)
        {
            if (token.Kind == GoogleSqlToken.Identifier)
            {
                var text = token.ToStringValue();
                if (KeywordTokens.TryGetValue(text, out var keyword))
                {
                    resolved.Add(new Token<GoogleSqlToken>(keyword, token.Span));
                }
                else
                {
                    resolved.Add(token);
                }
            }
            else
            {
                resolved.Add(token);
            }
        }

        return new TokenList<GoogleSqlToken>(resolved.ToArray());
    }
}
