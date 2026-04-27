using System.Globalization;
using System.Text.RegularExpressions;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable;

/// <summary>
/// Executes parsed GoogleSQL queries against in-memory Bigtable data.
/// Each row is exposed as a dictionary of column family names → map of qualifier → value.
///
/// Bigtable GoogleSQL data model:
///   - _key (BYTES) — row key
///   - Each column family is a MAP&lt;BYTES, BYTES&gt; column
///   - family['qualifier'] returns the latest cell value (BYTES)
///   - CAST is pervasive: CAST(family['qualifier'] AS STRING), CAST(... AS INT64), etc.
/// </summary>
internal sealed class GoogleSqlExecutor
{
    private readonly TableData _table;
    private readonly IReadOnlyDictionary<string, Google.Cloud.Bigtable.V2.Value>? _parameters;

    public GoogleSqlExecutor(TableData table,
        IReadOnlyDictionary<string, Google.Cloud.Bigtable.V2.Value>? parameters = null)
    {
        _table = table;
        _parameters = parameters;
    }

    /// <summary>
    /// Executes a SELECT query and returns rows as dictionaries of column name → value.
    /// </summary>
    public IReadOnlyList<Dictionary<string, object?>> Execute(SelectQuery query)
    {
        // Read all rows from the table
        var rows = _table.ReadRows().ToList();

        // Build row contexts
        var contexts = rows.Select(BuildRowContext).ToList();

        // WHERE
        if (query.Where != null)
        {
            contexts = contexts.Where(ctx =>
            {
                var result = Evaluate(query.Where, ctx);
                return result is true;
            }).ToList();
        }

        // GROUP BY
        if (query.GroupBy is { Count: > 0 })
        {
            var grouped = contexts.GroupBy(ctx =>
            {
                var key = string.Join("|",
                    query.GroupBy.Select(g => Evaluate(g, ctx)?.ToString() ?? "NULL"));
                return key;
            });

            var aggregatedContexts = new List<Dictionary<string, object?>>();
            foreach (var group in grouped)
            {
                var groupList = group.ToList();
                var resultContext = new Dictionary<string, object?>(groupList[0]);
                resultContext["__group__"] = groupList;
                aggregatedContexts.Add(resultContext);
            }

            contexts = aggregatedContexts;

            // HAVING
            if (query.Having != null)
            {
                contexts = contexts.Where(ctx =>
                {
                    var result = Evaluate(query.Having, ctx);
                    return result is true;
                }).ToList();
            }
        }

        // SELECT columns
        // Check if any columns contain window functions — need to inject window context
        bool hasWindowFunctions = query.Columns.Any(c => ContainsWindowExpression(c.Expression));
        if (hasWindowFunctions)
        {
            // Inject window context into all rows
            for (int i = 0; i < contexts.Count; i++)
            {
                contexts[i]["__window_rows__"] = contexts;
                contexts[i]["__window_index__"] = i;
            }
        }

        var results = new List<Dictionary<string, object?>>();
        foreach (var ctx in contexts)
        {
            var row = new Dictionary<string, object?>();
            foreach (var col in query.Columns)
            {
                if (col.Expression is StarExpression)
                {
                    // Expand all columns
                    foreach (var (key, value) in ctx)
                    {
                        if (!key.StartsWith("__"))
                            row[key] = value;
                    }
                }
                else
                {
                    var value = Evaluate(col.Expression, ctx);
                    var name = col.Alias ?? InferColumnName(col.Expression);
                    row[name] = value;
                }
            }
            results.Add(row);
        }

        // DISTINCT
        if (query.Distinct)
        {
            results = results.DistinctBy(r =>
                string.Join("|", r.OrderBy(kv => kv.Key)
                    .Select(kv => $"{kv.Key}={kv.Value}"))).ToList();
        }

        // ORDER BY
        if (query.OrderBy is { Count: > 0 })
        {
            IOrderedEnumerable<Dictionary<string, object?>>? ordered = null;
            for (int i = 0; i < query.OrderBy.Count; i++)
            {
                var item = query.OrderBy[i];
                var idx = i;
                if (idx == 0)
                {
                    ordered = item.Descending
                        ? results.OrderByDescending(r => GetSortKey(item.Expression, r))
                        : results.OrderBy(r => GetSortKey(item.Expression, r));
                }
                else
                {
                    ordered = item.Descending
                        ? ordered!.ThenByDescending(r => GetSortKey(item.Expression, r))
                        : ordered!.ThenBy(r => GetSortKey(item.Expression, r));
                }
            }
            results = ordered!.ToList();
        }

        // OFFSET
        if (query.Offset.HasValue)
        {
            results = results.Skip((int)query.Offset.Value).ToList();
        }

        // LIMIT
        if (query.Limit.HasValue)
        {
            results = results.Take((int)query.Limit.Value).ToList();
        }

        return results;
    }

    /// <summary>
    /// Builds a row context dictionary from a RowData.
    /// _key = row key bytes; each family name = nested dictionary of qualifier → latest value.
    /// </summary>
    private Dictionary<string, object?> BuildRowContext(RowData row)
    {
        var ctx = new Dictionary<string, object?>
        {
            ["_key"] = row.Key.ToByteArray(),
        };

        var cells = row.GetCells();

        // Build family maps: family → { qualifier → latest value }
        foreach (var cell in cells)
        {
            if (!ctx.TryGetValue(cell.Family, out var existing) || existing is not Dictionary<string, byte[]> familyMap)
            {
                familyMap = new Dictionary<string, byte[]>();
                ctx[cell.Family] = familyMap;
            }

            var qualKey = cell.Qualifier.ToStringUtf8();
            // GetCells returns timestamp descending within a column, so first one wins (latest)
            familyMap.TryAdd(qualKey, cell.Value.ToByteArray());
        }

        return ctx;
    }

    /// <summary>
    /// Evaluates an expression against a row context.
    /// </summary>
    internal object? Evaluate(SqlExpression expr, Dictionary<string, object?> ctx)
    {
        return expr switch
        {
            LiteralExpression lit => lit.Value,
            ColumnRefExpression col => ctx.TryGetValue(col.Name, out var v) ? v : null,
            ParameterRefExpression param => EvaluateParameter(param.Name),
            MapSubscriptExpression sub => EvaluateMapSubscript(sub, ctx),
            MemberAccessExpression mem => EvaluateMemberAccess(mem, ctx),
            BinaryExpression bin => EvaluateBinary(bin, ctx),
            UnaryExpression un => EvaluateUnary(un, ctx),
            FunctionCallExpression func => EvaluateFunction(func, ctx),
            CastExpression cast => EvaluateCast(cast, ctx),
            IsNullExpression isNull => EvaluateIsNull(isNull, ctx),
            BetweenExpression between => EvaluateBetween(between, ctx),
            InExpression inExpr => EvaluateIn(inExpr, ctx),
            LikeExpression like => EvaluateLike(like, ctx),
            CaseExpression caseExpr => EvaluateCase(caseExpr, ctx),
            WindowExpression window => EvaluateWindow(window, ctx),
            _ => null,
        };
    }

    private object? EvaluateParameter(string name)
    {
        if (_parameters == null || !_parameters.TryGetValue(name, out var value))
            return null;

        return value.KindCase switch
        {
            Google.Cloud.Bigtable.V2.Value.KindOneofCase.IntValue => value.IntValue,
            Google.Cloud.Bigtable.V2.Value.KindOneofCase.StringValue => value.StringValue,
            Google.Cloud.Bigtable.V2.Value.KindOneofCase.BoolValue => value.BoolValue,
            Google.Cloud.Bigtable.V2.Value.KindOneofCase.FloatValue => value.FloatValue,
            Google.Cloud.Bigtable.V2.Value.KindOneofCase.BytesValue => value.BytesValue.ToByteArray(),
            Google.Cloud.Bigtable.V2.Value.KindOneofCase.RawValue => value.RawValue.ToByteArray(),
            _ => null,
        };
    }

    private object? EvaluateMapSubscript(MapSubscriptExpression sub, Dictionary<string, object?> ctx)
    {
        var map = Evaluate(sub.Map, ctx);
        var key = Evaluate(sub.Key, ctx);

        if (map is Dictionary<string, byte[]> familyMap && key is string strKey)
        {
            return familyMap.TryGetValue(strKey, out var val) ? val : null;
        }

        return null;
    }

    private object? EvaluateMemberAccess(MemberAccessExpression mem, Dictionary<string, object?> ctx)
    {
        // In Bigtable SQL, member access isn't standard — treat as nested column ref
        var obj = Evaluate(mem.Object, ctx);
        if (obj is Dictionary<string, byte[]> map)
        {
            return map.TryGetValue(mem.Member, out var val) ? val : null;
        }
        return null;
    }

    private object? EvaluateBinary(BinaryExpression bin, Dictionary<string, object?> ctx)
    {
        var left = Evaluate(bin.Left, ctx);
        var right = Evaluate(bin.Right, ctx);

        // Short-circuit for AND/OR
        if (bin.Op == BinaryOp.And)
        {
            if (left is not true) return false;
            return right is true;
        }
        if (bin.Op == BinaryOp.Or)
        {
            if (left is true) return true;
            return right is true;
        }

        // Null propagation
        if (left == null || right == null)
        {
            return bin.Op switch
            {
                BinaryOp.Equal => left == null && right == null,
                BinaryOp.NotEqual => !(left == null && right == null),
                _ => null,
            };
        }

        // Numeric operations
        if (ToDouble(left, out var ld) && ToDouble(right, out var rd))
        {
            return bin.Op switch
            {
                BinaryOp.Add => ld + rd,
                BinaryOp.Subtract => ld - rd,
                BinaryOp.Multiply => ld * rd,
                BinaryOp.Divide => rd != 0 ? ld / rd : null,
                BinaryOp.Modulo => rd != 0 ? ld % rd : null,
                BinaryOp.Equal => ld == rd,
                BinaryOp.NotEqual => ld != rd,
                BinaryOp.LessThan => ld < rd,
                BinaryOp.GreaterThan => ld > rd,
                BinaryOp.LessOrEqual => ld <= rd,
                BinaryOp.GreaterOrEqual => ld >= rd,
                _ => null,
            };
        }

        // String comparison
        var ls = left.ToString()!;
        var rs = right.ToString()!;
        var cmp = string.Compare(ls, rs, StringComparison.Ordinal);

        return bin.Op switch
        {
            BinaryOp.Add when left is string || right is string => ls + rs,
            BinaryOp.Equal => cmp == 0,
            BinaryOp.NotEqual => cmp != 0,
            BinaryOp.LessThan => cmp < 0,
            BinaryOp.GreaterThan => cmp > 0,
            BinaryOp.LessOrEqual => cmp <= 0,
            BinaryOp.GreaterOrEqual => cmp >= 0,
            _ => null,
        };
    }

    private object? EvaluateUnary(UnaryExpression un, Dictionary<string, object?> ctx)
    {
        var operand = Evaluate(un.Operand, ctx);
        return un.Op switch
        {
            UnaryOp.Negate when ToDouble(operand, out var d) => -d,
            UnaryOp.Not when operand is bool b => !b,
            _ => null,
        };
    }

    private object? EvaluateFunction(FunctionCallExpression func, Dictionary<string, object?> ctx)
    {
        var args = func.Arguments.Select(a => Evaluate(a, ctx)).ToList();

        return func.Name switch
        {
            // Aggregate functions (work on __group__ if present)
            "COUNT" => EvaluateAggregate(func, ctx, "COUNT"),
            "SUM" => EvaluateAggregate(func, ctx, "SUM"),
            "AVG" => EvaluateAggregate(func, ctx, "AVG"),
            "MIN" => EvaluateAggregate(func, ctx, "MIN"),
            "MAX" => EvaluateAggregate(func, ctx, "MAX"),

            // String functions
            "CONCAT" => string.Concat(args.Select(a => a?.ToString() ?? "")),
            "LENGTH" => args[0] is string s ? (object)s.Length
                : args[0] is byte[] b ? b.Length : null,
            "LOWER" => args[0]?.ToString()?.ToLowerInvariant(),
            "UPPER" => args[0]?.ToString()?.ToUpperInvariant(),
            "TRIM" => args[0]?.ToString()?.Trim(),
            "LTRIM" => args[0]?.ToString()?.TrimStart(),
            "RTRIM" => args[0]?.ToString()?.TrimEnd(),
            "SUBSTR" or "SUBSTRING" => EvaluateSubstring(args),
            "REPLACE" => args is [string orig, string from, string to]
                ? orig.Replace(from, to) : null,
            "REVERSE" => args[0] is string sr ? new string(sr.Reverse().ToArray()) : null,
            "STARTS_WITH" => args is [string ss, string sp]
                ? ss.StartsWith(sp, StringComparison.Ordinal) : null,
            "ENDS_WITH" => args is [string es, string ep]
                ? es.EndsWith(ep, StringComparison.Ordinal) : null,
            "STRPOS" or "INSTR" => args is [string haystack, string needle]
                ? (object)(haystack.IndexOf(needle, StringComparison.Ordinal) + 1) : null,
            "REGEXP_CONTAINS" => args is [string rc, string rp]
                ? Regex.IsMatch(rc, rp) : null,
            "LPAD" => EvaluateLPad(args),
            "RPAD" => EvaluateRPad(args),
            "LEFT" => args is [string leftS, ..] && ToDouble(args[1], out var leftN)
                ? leftS[..Math.Min((int)leftN, leftS.Length)] : null,
            "RIGHT" => args is [string rightS, ..] && ToDouble(args[1], out var rightN)
                ? rightS[Math.Max(0, rightS.Length - (int)rightN)..] : null,
            "REGEXP_EXTRACT" => args is [string reS, string rePat]
                ? Regex.Match(reS, rePat) is { Success: true } reM ? reM.Groups.Count > 1 ? reM.Groups[1].Value : reM.Value : null : null,
            "REGEXP_EXTRACT_ALL" => args is [string reaS, string reaPat]
                ? (object)Regex.Matches(reaS, reaPat).Select(m => m.Groups.Count > 1 ? m.Groups[1].Value : m.Value).ToList() : null,
            "REGEXP_REPLACE" => args is [string rrS, string rrPat, string rrRepl]
                ? Regex.Replace(rrS, rrPat, rrRepl) : null,
            "REGEXP_INSTR" => args is [string riS, string riPat]
                ? (object)(Regex.Match(riS, riPat) is { Success: true } rim ? rim.Index + 1 : 0) : null,

            // Math functions
            "ABS" => ToDouble(args[0], out var absV) ? Math.Abs(absV) : null,
            "CEIL" or "CEILING" => ToDouble(args[0], out var ceilV) ? Math.Ceiling(ceilV) : null,
            "FLOOR" => ToDouble(args[0], out var floorV) ? Math.Floor(floorV) : null,
            "ROUND" => EvaluateRound(args),
            "SIGN" => ToDouble(args[0], out var signV) ? (object)Math.Sign(signV) : null,
            "SQRT" => ToDouble(args[0], out var sqrtV) ? Math.Sqrt(sqrtV) : null,
            "POWER" or "POW" => args.Count >= 2 && ToDouble(args[0], out var powB)
                && ToDouble(args[1], out var powE) ? Math.Pow(powB, powE) : null,
            "EXP" => ToDouble(args[0], out var expV) ? Math.Exp(expV) : null,
            "LOG" => ToDouble(args[0], out var logV) ? Math.Log(logV) : null,
            "LOG10" => ToDouble(args[0], out var log10V) ? Math.Log10(log10V) : null,
            "MOD" => args.Count >= 2 && ToDouble(args[0], out var modA)
                && ToDouble(args[1], out var modB) && modB != 0 ? modA % modB : null,
            "TRUNC" => ToDouble(args[0], out var truncV) ? Math.Truncate(truncV) : null,
            "SIN" => ToDouble(args[0], out var sinV) ? Math.Sin(sinV) : null,
            "COS" => ToDouble(args[0], out var cosV) ? Math.Cos(cosV) : null,
            "TAN" => ToDouble(args[0], out var tanV) ? Math.Tan(tanV) : null,
            "ASIN" => ToDouble(args[0], out var asinV) ? Math.Asin(asinV) : null,
            "ACOS" => ToDouble(args[0], out var acosV) ? Math.Acos(acosV) : null,
            "ATAN" => ToDouble(args[0], out var atanV) ? Math.Atan(atanV) : null,
            "ATAN2" => args.Count >= 2 && ToDouble(args[0], out var a2y)
                && ToDouble(args[1], out var a2x) ? Math.Atan2(a2y, a2x) : null,

            // Conversion
            "TO_HEX" => args[0] is byte[] hexBytes
                ? Convert.ToHexString(hexBytes).ToLowerInvariant() : null,
            "FROM_HEX" => args[0] is string hexStr
                ? (object)Convert.FromHexString(hexStr) : null,
            "TO_BASE64" => args[0] is byte[] b64Bytes
                ? Convert.ToBase64String(b64Bytes) : null,
            "FROM_BASE64" => args[0] is string b64Str
                ? (object)Convert.FromBase64String(b64Str) : null,
            "CODE_POINTS_TO_BYTES" => args[0] is List<object?> codePoints
                ? (object)codePoints.Where(cp => cp != null)
                    .Select(cp => (byte)Convert.ToInt64(cp!, CultureInfo.InvariantCulture)).ToArray()
                : null,

            // Map functions
            // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2
            "MAP_CONTAINS_KEY" => args is [Dictionary<string, byte[]> mckMap, string mckKey]
                ? (object)mckMap.ContainsKey(mckKey) : null,
            "MAP_EMPTY" => args[0] is Dictionary<string, byte[]> meMap
                ? (object)(meMap.Count == 0) : null,
            "MAP_KEYS" => args[0] is Dictionary<string, byte[]> mkMap
                ? (object)mkMap.Keys.ToList() : null,
            "MAP_VALUES" => args[0] is Dictionary<string, byte[]> mvMap
                ? (object)mvMap.Values.Select(v => (object?)v).ToList() : null,
            "MAP_ENTRIES" => args[0] is Dictionary<string, byte[]> menMap
                ? (object)menMap.Select(kv => new Dictionary<string, object?> { ["key"] = kv.Key, ["value"] = kv.Value }).ToList()
                : null,

            // Array functions
            "ARRAY_LENGTH" => args[0] is IList<object?> alArr ? (object)alArr.Count : null,
            "ARRAY_CONCAT" => EvaluateArrayConcat(args),
            "GENERATE_ARRAY" => EvaluateGenerateArray(args),

            // Statistical / approximate functions (simplified implementations)
            "APPROX_COUNT_DISTINCT" => EvaluateAggregate(func, ctx, "COUNT"), // Simplified: exact count
            "STDDEV" or "STDDEV_SAMP" => EvaluateStddev(func, ctx, false),
            "STDDEV_POP" => EvaluateStddev(func, ctx, true),
            "VARIANCE" or "VAR_SAMP" => EvaluateVariance(func, ctx, false),
            "VAR_POP" => EvaluateVariance(func, ctx, true),

            // Conditional
            "IF" => args.Count >= 3
                ? (args[0] is true ? args[1] : args[2]) : null,
            "IFNULL" or "COALESCE" => args.FirstOrDefault(a => a != null),

            _ => null, // Unknown function returns null
        };
    }

    private object? EvaluateAggregate(FunctionCallExpression func,
        Dictionary<string, object?> ctx, string aggName)
    {
        if (!ctx.TryGetValue("__group__", out var groupObj) || groupObj is not List<Dictionary<string, object?>> group)
        {
            // Not in a group — apply to single row (degenerate case)
            if (aggName == "COUNT") return (long)1;
            if (func.Arguments.Count > 0)
                return Evaluate(func.Arguments[0], ctx);
            return null;
        }

        if (aggName == "COUNT")
        {
            if (func.Arguments.Count == 0 || func.Arguments[0] is StarExpression)
                return (long)group.Count;
            return (long)group.Count(r => Evaluate(func.Arguments[0], r) != null);
        }

        var values = group
            .Select(r => Evaluate(func.Arguments[0], r))
            .Where(v => v != null)
            .ToList();

        return aggName switch
        {
            "SUM" => values.Sum(v => ToDoubleVal(v)),
            "AVG" => values.Count > 0 ? values.Average(v => ToDoubleVal(v)) : null,
            "MIN" => values.Count > 0 ? values.Min(v => ToDoubleVal(v)) : null,
            "MAX" => values.Count > 0 ? values.Max(v => ToDoubleVal(v)) : null,
            _ => null,
        };
    }

    private object? EvaluateCast(CastExpression cast, Dictionary<string, object?> ctx)
    {
        var value = Evaluate(cast.Operand, ctx);
        if (value == null) return null;

        try
        {
            return cast.TargetType switch
            {
                SqlType.String => value switch
                {
                    byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
                    _ => value.ToString(),
                },
                SqlType.Int64 => value switch
                {
                    byte[] bytes when bytes.Length >= 8 =>
                        System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(bytes),
                    double d => (long)d,
                    string s => long.Parse(s, CultureInfo.InvariantCulture),
                    long l => l,
                    _ => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                },
                SqlType.Float64 => value switch
                {
                    string s => double.Parse(s, CultureInfo.InvariantCulture),
                    _ => Convert.ToDouble(value, CultureInfo.InvariantCulture),
                },
                SqlType.Bool => value switch
                {
                    string s => bool.Parse(s),
                    long l => l != 0,
                    _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
                },
                SqlType.Bytes => value switch
                {
                    string s => System.Text.Encoding.UTF8.GetBytes(s),
                    byte[] b => b,
                    _ => null,
                },
                _ => value,
            };
        }
        catch when (cast.Safe)
        {
            return null; // SAFE_CAST returns null on failure
        }
    }

    private object? EvaluateIsNull(IsNullExpression isNull, Dictionary<string, object?> ctx)
    {
        var value = Evaluate(isNull.Operand, ctx);
        return isNull.Negated ? value != null : value == null;
    }

    private object? EvaluateBetween(BetweenExpression between, Dictionary<string, object?> ctx)
    {
        var value = Evaluate(between.Operand, ctx);
        var low = Evaluate(between.Low, ctx);
        var high = Evaluate(between.High, ctx);

        if (value == null || low == null || high == null) return null;

        if (ToDouble(value, out var dv) && ToDouble(low, out var dl) && ToDouble(high, out var dh))
        {
            return dv >= dl && dv <= dh;
        }

        var sv = value.ToString()!;
        var sl = low.ToString()!;
        var sh = high.ToString()!;
        return string.Compare(sv, sl, StringComparison.Ordinal) >= 0
            && string.Compare(sv, sh, StringComparison.Ordinal) <= 0;
    }

    private object? EvaluateIn(InExpression inExpr, Dictionary<string, object?> ctx)
    {
        var value = Evaluate(inExpr.Operand, ctx);
        if (value == null) return null;

        foreach (var item in inExpr.Values)
        {
            var itemVal = Evaluate(item, ctx);
            if (itemVal != null && value.ToString() == itemVal.ToString())
                return true;
        }
        return false;
    }

    private object? EvaluateLike(LikeExpression like, Dictionary<string, object?> ctx)
    {
        var value = Evaluate(like.Operand, ctx)?.ToString();
        var pattern = Evaluate(like.Pattern, ctx)?.ToString();
        if (value == null || pattern == null) return null;

        // Convert SQL LIKE pattern to regex
        var regex = "^" + Regex.Escape(pattern)
            .Replace("%", ".*")
            .Replace("_", ".") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
    }

    private object? EvaluateCase(CaseExpression caseExpr, Dictionary<string, object?> ctx)
    {
        foreach (var (condition, result) in caseExpr.WhenClauses)
        {
            var condValue = Evaluate(condition, ctx);
            if (condValue is true)
            {
                return Evaluate(result, ctx);
            }
        }
        return caseExpr.ElseResult != null ? Evaluate(caseExpr.ElseResult, ctx) : null;
    }

    private static object? EvaluateSubstring(IReadOnlyList<object?> args)
    {
        if (args[0] is not string s) return null;
        if (!ToDouble(args[1], out var start)) return null;
        var startIdx = Math.Max(0, (int)start - 1); // SQL is 1-based
        if (startIdx >= s.Length) return "";
        if (args.Count >= 3 && ToDouble(args[2], out var len))
        {
            var length = Math.Min((int)len, s.Length - startIdx);
            return s.Substring(startIdx, Math.Max(0, length));
        }
        return s[startIdx..];
    }

    private static object? EvaluateLPad(IReadOnlyList<object?> args)
    {
        if (args[0] is not string s || !ToDouble(args[1], out var len)) return null;
        var pad = args.Count >= 3 ? args[2]?.ToString() ?? " " : " ";
        var target = (int)len;
        while (s.Length < target) s = pad + s;
        return s[..target];
    }

    private static object? EvaluateRPad(IReadOnlyList<object?> args)
    {
        if (args[0] is not string s || !ToDouble(args[1], out var len)) return null;
        var pad = args.Count >= 3 ? args[2]?.ToString() ?? " " : " ";
        var target = (int)len;
        while (s.Length < target) s += pad;
        return s[..target];
    }

    private static object? EvaluateRound(IReadOnlyList<object?> args)
    {
        if (!ToDouble(args[0], out var v)) return null;
        if (args.Count >= 2 && ToDouble(args[1], out var digits))
            return Math.Round(v, (int)digits, MidpointRounding.AwayFromZero);
        return Math.Round(v, MidpointRounding.AwayFromZero);
    }

    private static object? EvaluateArrayConcat(IReadOnlyList<object?> args)
    {
        var result = new List<object?>();
        foreach (var arg in args)
        {
            if (arg is IList<object?> list) result.AddRange(list);
        }
        return result;
    }

    private static object? EvaluateGenerateArray(IReadOnlyList<object?> args)
    {
        if (args.Count < 2) return null;
        if (!ToDouble(args[0], out var start) || !ToDouble(args[1], out var end)) return null;
        var step = args.Count >= 3 && ToDouble(args[2], out var s) ? s : 1.0;
        var result = new List<object?>();
        for (var i = start; step > 0 ? i <= end : i >= end; i += step)
        {
            result.Add((long)i);
        }
        return result;
    }

    private object? EvaluateStddev(FunctionCallExpression func,
        Dictionary<string, object?> ctx, bool population)
    {
        if (!ctx.TryGetValue("__group__", out var groupObj) || groupObj is not List<Dictionary<string, object?>> group)
            return null;
        var values = group.Select(r => Evaluate(func.Arguments[0], r))
            .Where(v => v != null).Select(v => ToDoubleVal(v)).ToList();
        if (values.Count < 2 && !population) return null;
        var avg = values.Average();
        var variance = values.Sum(v => (v - avg) * (v - avg)) / (population ? values.Count : values.Count - 1);
        return Math.Sqrt(variance);
    }

    private object? EvaluateVariance(FunctionCallExpression func,
        Dictionary<string, object?> ctx, bool population)
    {
        if (!ctx.TryGetValue("__group__", out var groupObj) || groupObj is not List<Dictionary<string, object?>> group)
            return null;
        var values = group.Select(r => Evaluate(func.Arguments[0], r))
            .Where(v => v != null).Select(v => ToDoubleVal(v)).ToList();
        if (values.Count < 2 && !population) return null;
        var avg = values.Average();
        return values.Sum(v => (v - avg) * (v - avg)) / (population ? values.Count : values.Count - 1);
    }

    private static bool ToDouble(object? value, out double result)
    {
        result = 0;
        if (value == null) return false;
        if (value is double d) { result = d; return true; }
        if (value is long l) { result = l; return true; }
        if (value is int i) { result = i; return true; }
        if (value is float f) { result = f; return true; }
        if (value is decimal dec) { result = (double)dec; return true; }
        if (value is string s && double.TryParse(s, CultureInfo.InvariantCulture, out result)) return true;
        return false;
    }

    private static double ToDoubleVal(object? value)
    {
        ToDouble(value, out var d);
        return d;
    }

    private IComparable? GetSortKey(SqlExpression expr, Dictionary<string, object?> row)
    {
        // Re-evaluate against original row context... for ORDER BY we need the expression,
        // but we have result rows, not source rows. For column refs use the result row directly.
        var value = expr switch
        {
            ColumnRefExpression col => row.TryGetValue(col.Name, out var v) ? v : null,
            _ => null, // Simplified: ORDER BY must reference result column names
        };

        return value switch
        {
            double d => d,
            long l => l,
            int i => i,
            string s => s,
            byte[] b => Convert.ToBase64String(b),
            bool bv => bv,
            null => null,
            _ => value.ToString(),
        };
    }

    private static string InferColumnName(SqlExpression expr) => expr switch
    {
        ColumnRefExpression col => col.Name,
        FunctionCallExpression func => func.Name,
        WindowExpression win => win.Function.Name,
        MapSubscriptExpression => "value",
        CastExpression cast => InferColumnName(cast.Operand),
        _ => "expr",
    };

    /// <summary>
    /// Evaluates a window function. Window functions need access to all rows in the partition,
    /// which are stored in __window_rows__ context key during execution.
    /// </summary>
    private object? EvaluateWindow(WindowExpression window, Dictionary<string, object?> ctx)
    {
        // Window functions need the full row set — stored in __window_rows__ and __window_index__
        if (!ctx.TryGetValue("__window_rows__", out var rowsObj) ||
            rowsObj is not List<Dictionary<string, object?>> allRows)
            return null;

        if (!ctx.TryGetValue("__window_index__", out var idxObj) || idxObj is not int currentIdx)
            return null;

        // Partition the rows
        var partitionRows = allRows;
        if (window.PartitionBy is { Count: > 0 })
        {
            var currentPartitionKey = string.Join("|",
                window.PartitionBy.Select(p => Evaluate(p, ctx)?.ToString() ?? "NULL"));
            partitionRows = allRows.Where(r =>
            {
                var key = string.Join("|",
                    window.PartitionBy.Select(p => Evaluate(p, r)?.ToString() ?? "NULL"));
                return key == currentPartitionKey;
            }).ToList();
        }

        // Sort within partition if ORDER BY specified
        if (window.OrderBy is { Count: > 0 })
        {
            partitionRows = SortRows(partitionRows, window.OrderBy);
        }

        // Find current row's position within the partition
        var positionInPartition = partitionRows.IndexOf(ctx);
        if (positionInPartition < 0) positionInPartition = 0;

        var funcName = window.Function.Name;
        return funcName switch
        {
            "ROW_NUMBER" => (long)(positionInPartition + 1),
            "RANK" => EvaluateRank(partitionRows, positionInPartition, window.OrderBy, false),
            "DENSE_RANK" => EvaluateRank(partitionRows, positionInPartition, window.OrderBy, true),
            "NTILE" => window.Function.Arguments.Count > 0 && ToDouble(Evaluate(window.Function.Arguments[0], ctx), out var n)
                ? (long)((positionInPartition * (int)n / partitionRows.Count) + 1) : null,
            "LAG" => EvaluateLagLead(window.Function, partitionRows, positionInPartition, -1),
            "LEAD" => EvaluateLagLead(window.Function, partitionRows, positionInPartition, 1),
            "FIRST_VALUE" => window.Function.Arguments.Count > 0
                ? Evaluate(window.Function.Arguments[0], partitionRows[0]) : null,
            "LAST_VALUE" => window.Function.Arguments.Count > 0
                ? Evaluate(window.Function.Arguments[0], partitionRows[^1]) : null,
            "NTH_VALUE" => window.Function.Arguments.Count >= 2
                && ToDouble(Evaluate(window.Function.Arguments[1], ctx), out var nth)
                && (int)nth - 1 >= 0 && (int)nth - 1 < partitionRows.Count
                ? Evaluate(window.Function.Arguments[0], partitionRows[(int)nth - 1]) : null,
            _ => null,
        };
    }

    private long EvaluateRank(List<Dictionary<string, object?>> partition, int position,
        IReadOnlyList<OrderByItem>? orderBy, bool dense)
    {
        if (orderBy == null || orderBy.Count == 0 || position == 0)
            return 1;

        var currentKey = GetOrderByKey(partition[position], orderBy);
        long rank = 1;
        var distinctRanks = 1;
        for (int i = 0; i < position; i++)
        {
            var prevKey = GetOrderByKey(partition[i], orderBy);
            if (prevKey != currentKey)
            {
                if (dense) distinctRanks++;
                else rank = i + 1 + 1;
            }
        }
        return dense ? distinctRanks : rank;
    }

    private string GetOrderByKey(Dictionary<string, object?> row, IReadOnlyList<OrderByItem> orderBy)
    {
        return string.Join("|", orderBy.Select(o => Evaluate(o.Expression, row)?.ToString() ?? "NULL"));
    }

    private object? EvaluateLagLead(FunctionCallExpression func,
        List<Dictionary<string, object?>> partition, int position, int direction)
    {
        if (func.Arguments.Count == 0) return null;
        var offset = func.Arguments.Count >= 2 && ToDouble(Evaluate(func.Arguments[1], partition[position]), out var o)
            ? (int)o : 1;
        var targetIdx = position + (direction * offset);
        if (targetIdx < 0 || targetIdx >= partition.Count)
        {
            // Return default value if provided
            return func.Arguments.Count >= 3 ? Evaluate(func.Arguments[2], partition[position]) : null;
        }
        return Evaluate(func.Arguments[0], partition[targetIdx]);
    }

    private static List<Dictionary<string, object?>> SortRows(
        List<Dictionary<string, object?>> rows, IReadOnlyList<OrderByItem> orderBy)
    {
        // Simple multi-key sort
        IOrderedEnumerable<Dictionary<string, object?>>? ordered = null;
        for (int i = 0; i < orderBy.Count; i++)
        {
            var item = orderBy[i];
            Func<Dictionary<string, object?>, string> keyFunc = r =>
            {
                // Use column ref from result row
                if (item.Expression is ColumnRefExpression col && r.TryGetValue(col.Name, out var v))
                    return v?.ToString() ?? "";
                return "";
            };

            if (i == 0)
                ordered = item.Descending ? rows.OrderByDescending(keyFunc) : rows.OrderBy(keyFunc);
            else
                ordered = item.Descending ? ordered!.ThenByDescending(keyFunc) : ordered!.ThenBy(keyFunc);
        }
        return ordered?.ToList() ?? rows;
    }

    private static bool ContainsWindowExpression(SqlExpression expr) => expr switch
    {
        WindowExpression => true,
        AliasedExpression ae => ContainsWindowExpression(ae.Expression),
        _ => false,
    };
}
