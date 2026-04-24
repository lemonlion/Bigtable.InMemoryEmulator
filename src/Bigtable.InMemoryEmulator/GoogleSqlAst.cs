namespace Bigtable.InMemoryEmulator;

/// <summary>
/// Abstract syntax tree (AST) for a subset of GoogleSQL supported by Bigtable's ExecuteQuery.
/// Adapted from CosmosSqlParser's expression model.
/// </summary>

// ==================== Expressions ====================

internal abstract record SqlExpression;

internal sealed record LiteralExpression(object? Value, SqlType Type) : SqlExpression;

internal sealed record ColumnRefExpression(string Name) : SqlExpression;

internal sealed record MapSubscriptExpression(SqlExpression Map, SqlExpression Key) : SqlExpression;

internal sealed record MemberAccessExpression(SqlExpression Object, string Member) : SqlExpression;

internal sealed record ParameterRefExpression(string Name) : SqlExpression;

internal sealed record BinaryExpression(SqlExpression Left, BinaryOp Op, SqlExpression Right) : SqlExpression;

internal sealed record UnaryExpression(UnaryOp Op, SqlExpression Operand) : SqlExpression;

internal sealed record FunctionCallExpression(string Name, IReadOnlyList<SqlExpression> Arguments) : SqlExpression;

/// <summary>
/// Window function expression: func OVER (PARTITION BY ... ORDER BY ...)
/// Ref: GoogleSQL window functions — ROW_NUMBER, RANK, DENSE_RANK, LAG, LEAD, etc.
/// </summary>
internal sealed record WindowExpression(
    FunctionCallExpression Function,
    IReadOnlyList<SqlExpression>? PartitionBy,
    IReadOnlyList<OrderByItem>? OrderBy) : SqlExpression;

/// <summary>
/// Pipe syntax expression: source |> operation
/// Ref: GoogleSQL pipe syntax — sequential transformation pipeline
/// </summary>
internal sealed record PipeExpression(SqlExpression Source, SqlExpression Operation) : SqlExpression;

internal sealed record CastExpression(SqlExpression Operand, SqlType TargetType, bool Safe) : SqlExpression;

internal sealed record IsNullExpression(SqlExpression Operand, bool Negated) : SqlExpression;

internal sealed record BetweenExpression(SqlExpression Operand, SqlExpression Low, SqlExpression High) : SqlExpression;

internal sealed record InExpression(SqlExpression Operand, IReadOnlyList<SqlExpression> Values) : SqlExpression;

internal sealed record LikeExpression(SqlExpression Operand, SqlExpression Pattern) : SqlExpression;

internal sealed record CaseExpression(
    IReadOnlyList<(SqlExpression Condition, SqlExpression Result)> WhenClauses,
    SqlExpression? ElseResult) : SqlExpression;

internal sealed record StarExpression : SqlExpression;

internal sealed record AliasedExpression(SqlExpression Expression, string? Alias) : SqlExpression;

// ==================== Operators ====================

internal enum BinaryOp
{
    Add, Subtract, Multiply, Divide, Modulo,
    Equal, NotEqual, LessThan, GreaterThan, LessOrEqual, GreaterOrEqual,
    And, Or,
}

internal enum UnaryOp
{
    Negate, Not,
}

// ==================== Types ====================

internal enum SqlType
{
    Null, Int64, Float64, Float32, Bool, String, Bytes, Timestamp, Date, Array, Map, Struct,
}

// ==================== Query ====================

internal sealed record SelectQuery
{
    public bool Distinct { get; init; }
    public required IReadOnlyList<AliasedExpression> Columns { get; init; }
    public string? FromTable { get; init; }
    public SqlExpression? Where { get; init; }
    public IReadOnlyList<SqlExpression>? GroupBy { get; init; }
    public SqlExpression? Having { get; init; }
    public IReadOnlyList<OrderByItem>? OrderBy { get; init; }
    public long? Limit { get; init; }
    public long? Offset { get; init; }
}

internal sealed record OrderByItem(SqlExpression Expression, bool Descending);
