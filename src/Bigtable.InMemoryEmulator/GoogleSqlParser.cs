using Superpower;
using Superpower.Model;
using Superpower.Parsers;
using SP = Superpower.Parse;

namespace Bigtable.InMemoryEmulator;

internal static class GoogleSqlParser
{
    private static readonly TokenListParser<GoogleSqlToken, SqlExpression> ExpressionRef =
        SP.Ref(() => Expression!)!;

    private static readonly TokenListParser<GoogleSqlToken, SqlExpression> IntegerLiteral =
        Token.EqualTo(GoogleSqlToken.IntegerLiteral)
            .Select(t => (SqlExpression)new LiteralExpression(long.Parse(t.ToStringValue()), SqlType.Int64));

    private static readonly TokenListParser<GoogleSqlToken, SqlExpression> FloatLiteral =
        Token.EqualTo(GoogleSqlToken.FloatLiteral)
            .Select(t => (SqlExpression)new LiteralExpression(double.Parse(t.ToStringValue()), SqlType.Float64));

    private static readonly TokenListParser<GoogleSqlToken, SqlExpression> StringLiteral =
        Token.EqualTo(GoogleSqlToken.StringLiteral)
            .Select(t =>
            {
                var raw = t.ToStringValue();
                var value = raw.Length >= 2 ? raw[1..^1].Replace("''", "'") : raw;
                return (SqlExpression)new LiteralExpression(value, SqlType.String);
            });

    private static readonly TokenListParser<GoogleSqlToken, SqlExpression> BytesLiteral =
        Token.EqualTo(GoogleSqlToken.BytesLiteral)
            .Select(t =>
            {
                var raw = t.ToStringValue();
                var inner = raw.Length >= 3 ? raw[2..^1] : "";
                return (SqlExpression)new LiteralExpression(
                    System.Text.Encoding.UTF8.GetBytes(inner), SqlType.Bytes);
            });

    private static readonly TokenListParser<GoogleSqlToken, SqlExpression> BoolLiteral =
        Token.EqualTo(GoogleSqlToken.True).Value((SqlExpression)new LiteralExpression(true, SqlType.Bool))
        .Or(Token.EqualTo(GoogleSqlToken.False).Value((SqlExpression)new LiteralExpression(false, SqlType.Bool)));

    private static readonly TokenListParser<GoogleSqlToken, SqlExpression> NullLiteral =
        Token.EqualTo(GoogleSqlToken.Null).Value((SqlExpression)new LiteralExpression(null, SqlType.Null));

    private static readonly TokenListParser<GoogleSqlToken, SqlExpression> ParameterRef =
        Token.EqualTo(GoogleSqlToken.Parameter)
            .Select(t => { var name = t.ToStringValue(); if (name.StartsWith('@')) name = name[1..]; return (SqlExpression)new ParameterRefExpression(name); });

    private static readonly TokenListParser<GoogleSqlToken, SqlExpression> Star =
        Token.EqualTo(GoogleSqlToken.Star).Value((SqlExpression)new StarExpression());

    private static readonly TokenListParser<GoogleSqlToken, SqlType> TypeRef =
        Token.EqualTo(GoogleSqlToken.TypeInt64).Value(SqlType.Int64)
        .Or(Token.EqualTo(GoogleSqlToken.TypeFloat64).Value(SqlType.Float64))
        .Or(Token.EqualTo(GoogleSqlToken.TypeFloat32).Value(SqlType.Float32))
        .Or(Token.EqualTo(GoogleSqlToken.TypeBool).Value(SqlType.Bool))
        .Or(Token.EqualTo(GoogleSqlToken.TypeString).Value(SqlType.String))
        .Or(Token.EqualTo(GoogleSqlToken.TypeBytes).Value(SqlType.Bytes))
        .Or(Token.EqualTo(GoogleSqlToken.TypeTimestamp).Value(SqlType.Timestamp))
        .Or(Token.EqualTo(GoogleSqlToken.TypeDate).Value(SqlType.Date));

    private static readonly TokenListParser<GoogleSqlToken, SqlExpression> CastExpr =
        from keyword in Token.EqualTo(GoogleSqlToken.Cast).Or(Token.EqualTo(GoogleSqlToken.SafeCast))
        from lp in Token.EqualTo(GoogleSqlToken.LeftParen)
        from expr in ExpressionRef
        from asKw in Token.EqualTo(GoogleSqlToken.As)
        from type in TypeRef
        from rp in Token.EqualTo(GoogleSqlToken.RightParen)
        select (SqlExpression)new CastExpression(expr, type, keyword.Kind == GoogleSqlToken.SafeCast);

    private static readonly TokenListParser<GoogleSqlToken, (SqlExpression Condition, SqlExpression Result)> WhenClause =
        from whenKw in Token.EqualTo(GoogleSqlToken.When)
        from cond in ExpressionRef
        from thenKw in Token.EqualTo(GoogleSqlToken.Then)
        from result in ExpressionRef
        select (cond, result);

    private static readonly TokenListParser<GoogleSqlToken, SqlExpression> CaseExpr =
        from caseKw in Token.EqualTo(GoogleSqlToken.Case)
        from whens in WhenClause.AtLeastOnce()
        from elseResult in Token.EqualTo(GoogleSqlToken.Else).IgnoreThen(ExpressionRef).AsNullable().OptionalOrDefault()
        from endKw in Token.EqualTo(GoogleSqlToken.End)
        select (SqlExpression)new CaseExpression(whens, elseResult);


    private static readonly TokenListParser<GoogleSqlToken,
        (IReadOnlyList<SqlExpression>? PartitionBy, IReadOnlyList<OrderByItem>? OrderBy)> OverClauseInner =
        from overKw in Token.EqualTo(GoogleSqlToken.Over)
        from olp in Token.EqualTo(GoogleSqlToken.LeftParen)
        from partitionBy in (from partKw in Token.EqualTo(GoogleSqlToken.Partition)
            from byKw in Token.EqualTo(GoogleSqlToken.By)
            from exprs in ExpressionRef.ManyDelimitedBy(Token.EqualTo(GoogleSqlToken.Comma))
            select (IReadOnlyList<SqlExpression>?)exprs).OptionalOrDefault()
        from orderBy in (from orderKw in Token.EqualTo(GoogleSqlToken.Order)
            from byKw2 in Token.EqualTo(GoogleSqlToken.By)
            from items in (from expr in ExpressionRef
                from dir in Token.EqualTo(GoogleSqlToken.Desc).Value(true)
                    .Or(Token.EqualTo(GoogleSqlToken.Asc).Value(false)).OptionalOrDefault(false)
                select new OrderByItem(expr, dir)).ManyDelimitedBy(Token.EqualTo(GoogleSqlToken.Comma))
            select (IReadOnlyList<OrderByItem>?)items).OptionalOrDefault()
        from orp in Token.EqualTo(GoogleSqlToken.RightParen)
        select (partitionBy, orderBy);

    private static SqlExpression ApplyOver(string name, IReadOnlyList<SqlExpression> args,
        (IReadOnlyList<SqlExpression>? PartitionBy, IReadOnlyList<OrderByItem>? OrderBy)? over)
    {
        var func = new FunctionCallExpression(name, args);
        return over != null ? new WindowExpression(func, over.Value.PartitionBy, over.Value.OrderBy) : func;
    }

    private static readonly TokenListParser<GoogleSqlToken, SqlExpression> AggregateFunctionCall =
        from name in Token.EqualTo(GoogleSqlToken.Identifier)
            .Where(t => { var n = t.ToStringValue().ToUpperInvariant(); return n is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX"; })
        from lp in Token.EqualTo(GoogleSqlToken.LeftParen)
        from args in Star.Or(ExpressionRef).ManyDelimitedBy(Token.EqualTo(GoogleSqlToken.Comma))
        from rp in Token.EqualTo(GoogleSqlToken.RightParen)
        from over in OverClauseInner.Try().OptionalOrDefault()
        select ApplyOver(name.ToStringValue().ToUpperInvariant(), args,
            over.PartitionBy != null || over.OrderBy != null ? over : null);

    private static readonly TokenListParser<GoogleSqlToken, SqlExpression> FunctionCall =
        from name in Token.EqualTo(GoogleSqlToken.Identifier)
        from lp in Token.EqualTo(GoogleSqlToken.LeftParen)
        from args in ExpressionRef.ManyDelimitedBy(Token.EqualTo(GoogleSqlToken.Comma))
        from rp in Token.EqualTo(GoogleSqlToken.RightParen)
        from over in OverClauseInner.Try().OptionalOrDefault()
        select ApplyOver(name.ToStringValue().ToUpperInvariant(), args,
            over.PartitionBy != null || over.OrderBy != null ? over : null);
    private static readonly TokenListParser<GoogleSqlToken, SqlExpression> Parenthesized =
        from lp in Token.EqualTo(GoogleSqlToken.LeftParen)
        from expr in ExpressionRef
        from rp in Token.EqualTo(GoogleSqlToken.RightParen)
        select expr;

    private static readonly TokenListParser<GoogleSqlToken, SqlExpression> ColumnRef =
        Token.EqualTo(GoogleSqlToken.Identifier)
            .Select(t => (SqlExpression)new ColumnRefExpression(t.ToStringValue()));

    private static readonly TokenListParser<GoogleSqlToken, SqlExpression> Primary =
        CastExpr.Or(CaseExpr).Or(AggregateFunctionCall.Try()).Or(FunctionCall.Try())
        .Or(IntegerLiteral).Or(FloatLiteral).Or(StringLiteral).Or(BytesLiteral)
        .Or(BoolLiteral).Or(NullLiteral).Or(ParameterRef).Or(Parenthesized).Or(ColumnRef);

    private static readonly TokenListParser<GoogleSqlToken, Func<SqlExpression, SqlExpression>> Subscript =
        from lb in Token.EqualTo(GoogleSqlToken.LeftBracket)
        from key in ExpressionRef
        from rb in Token.EqualTo(GoogleSqlToken.RightBracket)
        select (Func<SqlExpression, SqlExpression>)(expr => new MapSubscriptExpression(expr, key));

    private static readonly TokenListParser<GoogleSqlToken, Func<SqlExpression, SqlExpression>> MemberAccess =
        from dot in Token.EqualTo(GoogleSqlToken.Dot)
        from member in Token.EqualTo(GoogleSqlToken.Identifier)
        select (Func<SqlExpression, SqlExpression>)(expr => new MemberAccessExpression(expr, member.ToStringValue()));

    private static readonly TokenListParser<GoogleSqlToken, SqlExpression> Postfix =
        from primary in Primary
        from suffixes in Subscript.Or(MemberAccess).Many()
        select suffixes.Aggregate(primary, (expr, fn) => fn(expr));

    private static readonly TokenListParser<GoogleSqlToken, SqlExpression> UnaryExpr =
        (from minus in Token.EqualTo(GoogleSqlToken.Minus)
         from operand in Postfix
         select (SqlExpression)new UnaryExpression(UnaryOp.Negate, operand))
        .Or(from notKw in Token.EqualTo(GoogleSqlToken.Not)
            from operand in Postfix
            select (SqlExpression)new UnaryExpression(UnaryOp.Not, operand))
        .Or(Postfix);

    private static readonly TokenListParser<GoogleSqlToken, BinaryOp> MultiplyOp =
        Token.EqualTo(GoogleSqlToken.Star).Value(BinaryOp.Multiply)
        .Or(Token.EqualTo(GoogleSqlToken.Slash).Value(BinaryOp.Divide))
        .Or(Token.EqualTo(GoogleSqlToken.Percent).Value(BinaryOp.Modulo));

    private static readonly TokenListParser<GoogleSqlToken, SqlExpression> Multiplicative =
        SP.Chain(MultiplyOp, UnaryExpr, (op, l, r) => new BinaryExpression(l, op, r));

    private static readonly TokenListParser<GoogleSqlToken, BinaryOp> AddOp =
        Token.EqualTo(GoogleSqlToken.Plus).Value(BinaryOp.Add)
        .Or(Token.EqualTo(GoogleSqlToken.Minus).Value(BinaryOp.Subtract));

    private static readonly TokenListParser<GoogleSqlToken, SqlExpression> Additive =
        SP.Chain(AddOp, Multiplicative, (op, l, r) => new BinaryExpression(l, op, r));

    private static readonly TokenListParser<GoogleSqlToken, BinaryOp> ComparisonOp =
        Token.EqualTo(GoogleSqlToken.Equal).Value(BinaryOp.Equal)
        .Or(Token.EqualTo(GoogleSqlToken.NotEqual).Value(BinaryOp.NotEqual))
        .Or(Token.EqualTo(GoogleSqlToken.NotEqual2).Value(BinaryOp.NotEqual))
        .Or(Token.EqualTo(GoogleSqlToken.LessOrEqual).Value(BinaryOp.LessOrEqual))
        .Or(Token.EqualTo(GoogleSqlToken.GreaterOrEqual).Value(BinaryOp.GreaterOrEqual))
        .Or(Token.EqualTo(GoogleSqlToken.LessThan).Value(BinaryOp.LessThan))
        .Or(Token.EqualTo(GoogleSqlToken.GreaterThan).Value(BinaryOp.GreaterThan));

    private static readonly TokenListParser<GoogleSqlToken, SqlExpression> Comparison =
        from left in Additive
        from rest in (
            (from isKw in Token.EqualTo(GoogleSqlToken.Is)
             from notKw in Token.EqualTo(GoogleSqlToken.Not).OptionalOrDefault()
             from nullKw in Token.EqualTo(GoogleSqlToken.Null)
             select (Func<SqlExpression, SqlExpression>)(l => new IsNullExpression(l, notKw.HasValue)))
            .Or(from betweenKw in Token.EqualTo(GoogleSqlToken.Between)
                from low in Additive
                from andKw in Token.EqualTo(GoogleSqlToken.And)
                from high in Additive
                select (Func<SqlExpression, SqlExpression>)(l => new BetweenExpression(l, low, high)))
            .Or(from inKw in Token.EqualTo(GoogleSqlToken.In)
                from lp in Token.EqualTo(GoogleSqlToken.LeftParen)
                from values in ExpressionRef.ManyDelimitedBy(Token.EqualTo(GoogleSqlToken.Comma))
                from rp in Token.EqualTo(GoogleSqlToken.RightParen)
                select (Func<SqlExpression, SqlExpression>)(l => new InExpression(l, values)))
            .Or(from likeKw in Token.EqualTo(GoogleSqlToken.Like)
                from pattern in Additive
                select (Func<SqlExpression, SqlExpression>)(l => new LikeExpression(l, pattern)))
            .Or(from op in ComparisonOp
                from right in Additive
                select (Func<SqlExpression, SqlExpression>)(l => new BinaryExpression(l, op, right)))
        ).OptionalOrDefault()
        select rest != null ? rest(left) : left;

    private static readonly TokenListParser<GoogleSqlToken, SqlExpression> AndExpr =
        SP.Chain(Token.EqualTo(GoogleSqlToken.And).Value(BinaryOp.And), Comparison,
            (op, l, r) => new BinaryExpression(l, op, r));

    private static readonly TokenListParser<GoogleSqlToken, SqlExpression> OrExpr =
        SP.Chain(Token.EqualTo(GoogleSqlToken.Or).Value(BinaryOp.Or), AndExpr,
            (op, l, r) => new BinaryExpression(l, op, r));

    private static readonly TokenListParser<GoogleSqlToken, SqlExpression> Expression = OrExpr;

    private static readonly TokenListParser<GoogleSqlToken, AliasedExpression> AliasedCol =
        from expr in Star.Or(ExpressionRef)
        from alias in (from asKw in Token.EqualTo(GoogleSqlToken.As).Optional()
            from id in Token.EqualTo(GoogleSqlToken.Identifier) select id.ToStringValue()).OptionalOrDefault()
        select new AliasedExpression(expr, alias);

    private static readonly TokenListParser<GoogleSqlToken, OrderByItem> OrderByItemParser =
        from expr in ExpressionRef
        from dir in Token.EqualTo(GoogleSqlToken.Desc).Value(true)
            .Or(Token.EqualTo(GoogleSqlToken.Asc).Value(false)).OptionalOrDefault(false)
        select new OrderByItem(expr, dir);

    public static readonly TokenListParser<GoogleSqlToken, SelectQuery> Query =
        from selectKw in Token.EqualTo(GoogleSqlToken.Select)
        from distinct in Token.EqualTo(GoogleSqlToken.Distinct).Optional()
        from columns in AliasedCol.ManyDelimitedBy(Token.EqualTo(GoogleSqlToken.Comma))
        from fromClause in (from fromKw in Token.EqualTo(GoogleSqlToken.From)
            from tableName in Token.EqualTo(GoogleSqlToken.Identifier)
            select tableName.ToStringValue()).OptionalOrDefault()
        from whereClause in (from whereKw in Token.EqualTo(GoogleSqlToken.Where)
            from expr in ExpressionRef select expr).AsNullable().OptionalOrDefault()
        from groupBy in (from groupKw in Token.EqualTo(GoogleSqlToken.Group)
            from byKw in Token.EqualTo(GoogleSqlToken.By)
            from exprs in ExpressionRef.ManyDelimitedBy(Token.EqualTo(GoogleSqlToken.Comma))
            select (IReadOnlyList<SqlExpression>)exprs).AsNullable().OptionalOrDefault()
        from having in (from havingKw in Token.EqualTo(GoogleSqlToken.Having)
            from expr in ExpressionRef select expr).AsNullable().OptionalOrDefault()
        from orderBy in (from orderKw in Token.EqualTo(GoogleSqlToken.Order)
            from byKw in Token.EqualTo(GoogleSqlToken.By)
            from items in OrderByItemParser.ManyDelimitedBy(Token.EqualTo(GoogleSqlToken.Comma))
            select (IReadOnlyList<OrderByItem>)items).AsNullable().OptionalOrDefault()
        from limit in (from limitKw in Token.EqualTo(GoogleSqlToken.Limit)
            from n in Token.EqualTo(GoogleSqlToken.IntegerLiteral)
            select (long?)long.Parse(n.ToStringValue())).OptionalOrDefault()
        from offset in (from offsetKw in Token.EqualTo(GoogleSqlToken.Offset)
            from n in Token.EqualTo(GoogleSqlToken.IntegerLiteral)
            select (long?)long.Parse(n.ToStringValue())).OptionalOrDefault()
        select new SelectQuery
        {
            Distinct = distinct.HasValue, Columns = columns, FromTable = fromClause,
            Where = whereClause, GroupBy = groupBy, Having = having,
            OrderBy = orderBy, Limit = limit, Offset = offset,
        };

    // ==================== Pipe Syntax ====================
    // Ref: GoogleSQL pipe syntax - sequential transformation pipeline
    // https://cloud.google.com/bigquery/docs/reference/standard-sql/pipe-syntax

    private static readonly TokenListParser<GoogleSqlToken, SelectQuery> FromOnly =
        from fromKw in Token.EqualTo(GoogleSqlToken.From)
        from tableName in Token.EqualTo(GoogleSqlToken.Identifier)
        select new SelectQuery
        {
            Columns = new[] { new AliasedExpression(new StarExpression(), null) },
            FromTable = tableName.ToStringValue(),
        };

    private static readonly TokenListParser<GoogleSqlToken, Func<SelectQuery, SelectQuery>> PipeOperation =
        from pipe in Token.EqualTo(GoogleSqlToken.Pipe)
        from op in (
            (from whereKw in Token.EqualTo(GoogleSqlToken.Where)
             from expr in ExpressionRef
             select (Func<SelectQuery, SelectQuery>)(q => q with { Where = expr }))
            .Or(from selectKw in Token.EqualTo(GoogleSqlToken.Select)
                from distinct in Token.EqualTo(GoogleSqlToken.Distinct).Optional()
                from cols in AliasedCol.ManyDelimitedBy(Token.EqualTo(GoogleSqlToken.Comma))
                select (Func<SelectQuery, SelectQuery>)(q => q with { Columns = cols, Distinct = distinct.HasValue }))
            .Or(from orderKw in Token.EqualTo(GoogleSqlToken.Order)
                from byKw in Token.EqualTo(GoogleSqlToken.By)
                from items in OrderByItemParser.ManyDelimitedBy(Token.EqualTo(GoogleSqlToken.Comma))
                select (Func<SelectQuery, SelectQuery>)(q => q with { OrderBy = items }))
            .Or(from limitKw in Token.EqualTo(GoogleSqlToken.Limit)
                from n in Token.EqualTo(GoogleSqlToken.IntegerLiteral)
                select (Func<SelectQuery, SelectQuery>)(q => q with { Limit = long.Parse(n.ToStringValue()) }))
        )
        select op;

    public static readonly TokenListParser<GoogleSqlToken, SelectQuery> FullQuery =
        from baseQuery in FromOnly.Try().Or(Query)
        from pipes in PipeOperation.Many()
        select pipes.Aggregate(baseQuery, (q, op) => op(q));

    public static SelectQuery ParseQuery(string sql)
    {
        var tokens = GoogleSqlTokenizer.Tokenize(sql);
        return FullQuery.Parse(tokens);
    }
}
