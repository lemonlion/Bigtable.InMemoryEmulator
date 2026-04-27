using System.Text.RegularExpressions;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace InMemoryEmulator.Bigtable;

/// <summary>
/// Evaluates RowFilter trees against row data.
/// Filters operate on a stream of cells within each row, independently testing each cell.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
///   "Takes a row as input and produces an alternate view of the row based on specified rules."
/// </summary>
internal static class RowFilterEvaluator
{
    /// <summary>
    /// Applies a RowFilter to a row's cells, returning the filtered cells.
    /// Returns empty list if no cells match.
    /// </summary>
    public static IReadOnlyList<CellData> Apply(RowFilter? filter, IReadOnlyList<CellData> cells, ByteString? rowKey = null)
    {
        if (filter == null)
            return cells;

        return ApplyFilter(filter, cells, rowKey, depth: 0);
    }

    /// <summary>
    /// Returns true if the filter matches at least one cell in the row.
    /// Used by CheckAndMutateRow predicate evaluation.
    /// </summary>
    public static bool Matches(RowFilter? filter, IReadOnlyList<CellData> cells, ByteString? rowKey = null)
    {
        if (filter == null)
            return cells.Count > 0;

        return ApplyFilter(filter, cells, rowKey, depth: 0).Count > 0;
    }

    /// <summary>
    /// Validates a RowFilter tree before evaluation.
    /// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
    ///   "RowFilter may not be nested to a depth of more than 20."
    ///   "Total serialized size must not exceed 20480 bytes."
    /// </summary>
    public static void Validate(RowFilter filter)
    {
        ValidateDepth(filter, depth: 0);
        ValidateSerializedSize(filter);
        ValidateLabelConstraints(filter);
    }

    private static IReadOnlyList<CellData> ApplyFilter(RowFilter filter, IReadOnlyList<CellData> cells, ByteString? rowKey, int depth)
    {
        if (depth > 20)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "RowFilter nesting exceeds maximum depth of 20."));
        }

        switch (filter.FilterCase)
        {
            case RowFilter.FilterOneofCase.Chain:
                return ApplyChain(filter.Chain, cells, rowKey, depth);

            case RowFilter.FilterOneofCase.Interleave:
                return ApplyInterleave(filter.Interleave, cells, rowKey, depth);

            case RowFilter.FilterOneofCase.Condition:
                return ApplyCondition(filter.Condition, cells, rowKey, depth);

            case RowFilter.FilterOneofCase.PassAllFilter:
                return filter.PassAllFilter ? cells : [];

            case RowFilter.FilterOneofCase.BlockAllFilter:
                return filter.BlockAllFilter ? [] : cells;

            case RowFilter.FilterOneofCase.RowKeyRegexFilter:
                return ApplyRowKeyRegex(filter.RowKeyRegexFilter, cells, rowKey);

            case RowFilter.FilterOneofCase.FamilyNameRegexFilter:
                return ApplyFamilyNameRegex(filter.FamilyNameRegexFilter, cells);

            case RowFilter.FilterOneofCase.ColumnQualifierRegexFilter:
                return ApplyColumnQualifierRegex(filter.ColumnQualifierRegexFilter, cells);

            case RowFilter.FilterOneofCase.ColumnRangeFilter:
                return ApplyColumnRange(filter.ColumnRangeFilter, cells);

            case RowFilter.FilterOneofCase.ValueRegexFilter:
                return ApplyValueRegex(filter.ValueRegexFilter, cells);

            case RowFilter.FilterOneofCase.ValueRangeFilter:
                return ApplyValueRange(filter.ValueRangeFilter, cells);

            case RowFilter.FilterOneofCase.TimestampRangeFilter:
                return ApplyTimestampRange(filter.TimestampRangeFilter, cells);

            case RowFilter.FilterOneofCase.CellsPerColumnLimitFilter:
                return ApplyCellsPerColumnLimit(filter.CellsPerColumnLimitFilter, cells);

            case RowFilter.FilterOneofCase.CellsPerRowLimitFilter:
                return ApplyCellsPerRowLimit(filter.CellsPerRowLimitFilter, cells);

            case RowFilter.FilterOneofCase.CellsPerRowOffsetFilter:
                return ApplyCellsPerRowOffset(filter.CellsPerRowOffsetFilter, cells);

            case RowFilter.FilterOneofCase.RowSampleFilter:
                return ApplyRowSample(filter.RowSampleFilter, cells);

            case RowFilter.FilterOneofCase.StripValueTransformer:
                return ApplyStripValue(filter.StripValueTransformer, cells);

            case RowFilter.FilterOneofCase.ApplyLabelTransformer:
                return ApplyLabelTransformer(filter.ApplyLabelTransformer, cells);

            case RowFilter.FilterOneofCase.Sink:
                // Ref: "Hook for introspection into the RowFilter."
                // Sink outputs cells directly to final output, bypassing parent filters.
                // For simplicity in the in-memory emulator, treat as PassAll.
                return filter.Sink ? cells : [];

            default:
                return cells;
        }
    }

    #region Composite Filters

    /// <summary>
    /// Chain: sequential pipeline — output of one feeds next.
    /// Ref: "The elements of `filters` are chained together to process the input row."
    /// </summary>
    private static IReadOnlyList<CellData> ApplyChain(RowFilter.Types.Chain chain, IReadOnlyList<CellData> cells, ByteString? rowKey, int depth)
    {
        IReadOnlyList<CellData> result = cells;
        foreach (var subFilter in chain.Filters)
        {
            result = ApplyFilter(subFilter, result, rowKey, depth + 1);
            if (result.Count == 0)
                break;
        }
        return result;
    }

    /// <summary>
    /// Interleave: parallel union — each sub-filter processes a copy of input, results are merged.
    /// Ref: "The elements of `filters` all process a copy of the input row, and the results are pooled."
    /// </summary>
    private static IReadOnlyList<CellData> ApplyInterleave(RowFilter.Types.Interleave interleave, IReadOnlyList<CellData> cells, ByteString? rowKey, int depth)
    {
        var results = new List<CellData>();
        foreach (var subFilter in interleave.Filters)
        {
            var subResult = ApplyFilter(subFilter, cells, rowKey, depth + 1);
            results.AddRange(subResult);
        }
        return results;
    }

    /// <summary>
    /// Condition: if predicate matches, apply true filter; else false filter.
    /// Ref: "A RowFilter which evaluates one of two possible RowFilters."
    /// Note: The in-memory implementation is fully atomic (simpler than production).
    /// </summary>
    private static IReadOnlyList<CellData> ApplyCondition(RowFilter.Types.Condition condition, IReadOnlyList<CellData> cells, ByteString? rowKey, int depth)
    {
        bool predicateMatched = condition.PredicateFilter != null &&
                                ApplyFilter(condition.PredicateFilter, cells, rowKey, depth + 1).Count > 0;

        if (predicateMatched && condition.TrueFilter != null)
        {
            return ApplyFilter(condition.TrueFilter, cells, rowKey, depth + 1);
        }
        else if (!predicateMatched && condition.FalseFilter != null)
        {
            return ApplyFilter(condition.FalseFilter, cells, rowKey, depth + 1);
        }

        return [];
    }

    #endregion

    #region Leaf Filters

    /// <summary>
    /// RowKeyRegexFilter: matches rows whose key matches the RE2 regex.
    /// Ref: "Matches only cells from rows whose keys satisfy the given RE2 regex."
    /// Note: We approximate RE2 with .NET Regex (covers 99%+ of patterns).
    ///       All regex filters use full-string matching (RE2 FullMatch), so we anchor with ^(?:...)$.
    /// </summary>
    private static IReadOnlyList<CellData> ApplyRowKeyRegex(ByteString pattern, IReadOnlyList<CellData> cells, ByteString? rowKey)
    {
        if (cells.Count == 0 || rowKey == null) return cells;

        // Ref: "Matches only cells from rows whose keys satisfy the given RE2 regex."
        // Bigtable regex filters use full-string matching (RE2 FullMatch semantics).
        var patternStr = pattern.ToStringUtf8();
        var regex = new Regex(AnchorPattern(patternStr), RegexOptions.Compiled);
        var keyStr = rowKey.ToStringUtf8();
        return regex.IsMatch(keyStr) ? cells : [];
    }

    /// <summary>
    /// FamilyNameRegexFilter: matches cells whose family name matches the regex.
    /// Ref: "Matches only cells from columns whose families satisfy the given RE2 regex."
    ///       Uses full-string matching (RE2 FullMatch), anchored with ^(?:...)$.
    /// </summary>
    private static IReadOnlyList<CellData> ApplyFamilyNameRegex(string pattern, IReadOnlyList<CellData> cells)
    {
        // Bigtable regex filters use full-string matching (RE2 FullMatch semantics).
        var regex = new Regex(AnchorPattern(pattern), RegexOptions.Compiled);
        return cells.Where(c => regex.IsMatch(c.Family)).ToList();
    }

    /// <summary>
    /// ColumnQualifierRegexFilter: matches cells whose qualifier matches the regex.
    /// Ref: "Matches only cells from columns whose qualifiers satisfy the given RE2 regex."
    ///       Uses full-string matching (RE2 FullMatch), anchored with ^(?:...)$.
    /// </summary>
    private static IReadOnlyList<CellData> ApplyColumnQualifierRegex(ByteString pattern, IReadOnlyList<CellData> cells)
    {
        // Bigtable regex filters use full-string matching (RE2 FullMatch semantics).
        var patternStr = pattern.ToStringUtf8();
        var regex = new Regex(AnchorPattern(patternStr), RegexOptions.Compiled);
        return cells.Where(c => regex.IsMatch(c.Qualifier.ToStringUtf8())).ToList();
    }

    /// <summary>
    /// ColumnRangeFilter: matches cells within a qualifier range in a specific family.
    /// Ref: "Matches only cells from columns within the given range."
    /// </summary>
    private static IReadOnlyList<CellData> ApplyColumnRange(ColumnRange range, IReadOnlyList<CellData> cells)
    {
        var cmp = ByteStringComparer.Instance;

        return cells.Where(c =>
        {
            if (c.Family != range.FamilyName)
                return false;

            // Start bound
            switch (range.StartQualifierCase)
            {
                case ColumnRange.StartQualifierOneofCase.StartQualifierClosed:
                    if (cmp.Compare(c.Qualifier, range.StartQualifierClosed) < 0) return false;
                    break;
                case ColumnRange.StartQualifierOneofCase.StartQualifierOpen:
                    if (cmp.Compare(c.Qualifier, range.StartQualifierOpen) <= 0) return false;
                    break;
            }

            // End bound
            switch (range.EndQualifierCase)
            {
                case ColumnRange.EndQualifierOneofCase.EndQualifierClosed:
                    if (cmp.Compare(c.Qualifier, range.EndQualifierClosed) > 0) return false;
                    break;
                case ColumnRange.EndQualifierOneofCase.EndQualifierOpen:
                    if (cmp.Compare(c.Qualifier, range.EndQualifierOpen) >= 0) return false;
                    break;
            }

            return true;
        }).ToList();
    }

    /// <summary>
    /// ValueRegexFilter: matches cells whose value matches the regex.
    /// Ref: "Matches only cells with values that satisfy the given regular expression."
    ///       Uses full-string matching (RE2 FullMatch), anchored with ^(?:...)$.
    /// </summary>
    private static IReadOnlyList<CellData> ApplyValueRegex(ByteString pattern, IReadOnlyList<CellData> cells)
    {
        // Bigtable regex filters use full-string matching (RE2 FullMatch semantics).
        var patternStr = pattern.ToStringUtf8();
        var regex = new Regex(AnchorPattern(patternStr), RegexOptions.Compiled);
        return cells.Where(c => regex.IsMatch(c.Value.ToStringUtf8())).ToList();
    }

    /// <summary>
    /// ValueRangeFilter: matches cells whose value falls within the range.
    /// Ref: "Matches only cells with values that fall within the given range."
    /// </summary>
    private static IReadOnlyList<CellData> ApplyValueRange(ValueRange range, IReadOnlyList<CellData> cells)
    {
        var cmp = ByteStringComparer.Instance;

        return cells.Where(c =>
        {
            switch (range.StartValueCase)
            {
                case ValueRange.StartValueOneofCase.StartValueClosed:
                    if (cmp.Compare(c.Value, range.StartValueClosed) < 0) return false;
                    break;
                case ValueRange.StartValueOneofCase.StartValueOpen:
                    if (cmp.Compare(c.Value, range.StartValueOpen) <= 0) return false;
                    break;
            }

            switch (range.EndValueCase)
            {
                case ValueRange.EndValueOneofCase.EndValueClosed:
                    if (cmp.Compare(c.Value, range.EndValueClosed) > 0) return false;
                    break;
                case ValueRange.EndValueOneofCase.EndValueOpen:
                    if (cmp.Compare(c.Value, range.EndValueOpen) >= 0) return false;
                    break;
            }

            return true;
        }).ToList();
    }

    /// <summary>
    /// TimestampRangeFilter: matches cells within [start, end) microsecond range.
    /// Ref: "Matches only cells with timestamps within the given range."
    /// </summary>
    private static IReadOnlyList<CellData> ApplyTimestampRange(TimestampRange range, IReadOnlyList<CellData> cells)
    {
        return cells.Where(c =>
        {
            if (range.StartTimestampMicros != 0 && c.TimestampMicros < range.StartTimestampMicros)
                return false;
            if (range.EndTimestampMicros != 0 && c.TimestampMicros >= range.EndTimestampMicros)
                return false;
            return true;
        }).ToList();
    }

    /// <summary>
    /// CellsPerColumnLimitFilter: returns first N cells per column (by descending timestamp).
    /// Ref: "Matches only the first N cells of each column."
    /// </summary>
    private static IReadOnlyList<CellData> ApplyCellsPerColumnLimit(int limit, IReadOnlyList<CellData> cells)
    {
        return cells
            .GroupBy(c => (c.Family, Qualifier: c.Qualifier.ToStringUtf8()))
            .SelectMany(g => g.Take(limit))
            .ToList();
    }

    /// <summary>
    /// CellsPerRowLimitFilter: returns first N cells in the row.
    /// Ref: "Matches only the first N cells of the row."
    /// </summary>
    private static IReadOnlyList<CellData> ApplyCellsPerRowLimit(int limit, IReadOnlyList<CellData> cells)
    {
        return cells.Take(limit).ToList();
    }

    /// <summary>
    /// CellsPerRowOffsetFilter: skips first N cells, returns rest.
    /// Ref: "Skips the first N cells of the row."
    /// </summary>
    private static IReadOnlyList<CellData> ApplyCellsPerRowOffset(int offset, IReadOnlyList<CellData> cells)
    {
        return cells.Skip(offset).ToList();
    }

    /// <summary>
    /// RowSampleFilter: randomly includes rows.
    /// Ref: "Matches all cells from a row with probability p."
    /// </summary>
    private static IReadOnlyList<CellData> ApplyRowSample(double probability, IReadOnlyList<CellData> cells)
    {
        // Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
        //   "row_sample_filter: Matches all cells from a row with probability p."
        //   The SDK validates p in [0.0, 1.0]; server rejects values outside that.
        if (probability < 0.0 || probability > 1.0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"row_sample_filter must be >= 0.0 and <= 1.0, got {probability}"));
        }

        if (cells.Count == 0) return cells;
        return Random.Shared.NextDouble() < probability ? cells : [];
    }

    /// <summary>
    /// StripValueTransformer: replaces cell values with empty bytes.
    /// Ref: "Replaces each cell's value with the empty string."
    /// </summary>
    private static IReadOnlyList<CellData> ApplyStripValue(bool strip, IReadOnlyList<CellData> cells)
    {
        if (!strip) return cells;
        return cells.Select(c => new CellData
        {
            Family = c.Family,
            Qualifier = c.Qualifier,
            TimestampMicros = c.TimestampMicros,
            Value = ByteString.Empty,
        }).ToList();
    }

    /// <summary>
    /// ApplyLabelTransformer: adds a label to all cells.
    /// Ref: "Applies the given label to all cells in the output row."
    ///   "Labels must be at most 15 characters in length, and match the RE2 pattern [a-z0-9\\-]+"
    /// </summary>
    private static IReadOnlyList<CellData> ApplyLabelTransformer(string label, IReadOnlyList<CellData> cells)
    {
        ValidateLabel(label);

        return cells.Select(c =>
        {
            var newCell = new CellData
            {
                Family = c.Family,
                Qualifier = c.Qualifier,
                TimestampMicros = c.TimestampMicros,
                Value = c.Value,
            };
            newCell.Labels.AddRange(c.Labels);
            newCell.Labels.Add(label);
            return newCell;
        }).ToList();
    }

    #endregion

    #region Validation

    private static void ValidateDepth(RowFilter filter, int depth)
    {
        if (depth > 20)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "RowFilter nesting exceeds maximum depth of 20."));
        }

        switch (filter.FilterCase)
        {
            case RowFilter.FilterOneofCase.Chain:
                foreach (var sub in filter.Chain.Filters)
                    ValidateDepth(sub, depth + 1);
                break;
            case RowFilter.FilterOneofCase.Interleave:
                foreach (var sub in filter.Interleave.Filters)
                    ValidateDepth(sub, depth + 1);
                break;
            case RowFilter.FilterOneofCase.Condition:
                if (filter.Condition.PredicateFilter != null)
                    ValidateDepth(filter.Condition.PredicateFilter, depth + 1);
                if (filter.Condition.TrueFilter != null)
                    ValidateDepth(filter.Condition.TrueFilter, depth + 1);
                if (filter.Condition.FalseFilter != null)
                    ValidateDepth(filter.Condition.FalseFilter, depth + 1);
                break;
        }
    }

    private static void ValidateSerializedSize(RowFilter filter)
    {
        // Ref: "total serialized size must not exceed 20480 bytes"
        int size = filter.CalculateSize();
        if (size > 20480)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"RowFilter serialized size ({size}) exceeds maximum of 20480 bytes."));
        }
    }

    private static void ValidateLabelConstraints(RowFilter filter)
    {
        ValidateLabelConstraintsInChain(filter, inChain: false, labelCount: 0);
    }

    private static int ValidateLabelConstraintsInChain(RowFilter filter, bool inChain, int labelCount)
    {
        switch (filter.FilterCase)
        {
            case RowFilter.FilterOneofCase.ApplyLabelTransformer:
                ValidateLabel(filter.ApplyLabelTransformer);
                if (inChain)
                {
                    labelCount++;
                    if (labelCount > 1)
                    {
                        // Ref: "A Chain may have no more than one sub-filter which contains a apply_label_transformer"
                        throw new RpcException(new Status(StatusCode.InvalidArgument,
                            "A Chain may have no more than one sub-filter containing ApplyLabelTransformer."));
                    }
                }
                return labelCount;

            case RowFilter.FilterOneofCase.Chain:
                int chainLabels = 0;
                foreach (var sub in filter.Chain.Filters)
                {
                    chainLabels = ValidateLabelConstraintsInChain(sub, inChain: true, chainLabels);
                }
                return labelCount;

            case RowFilter.FilterOneofCase.Interleave:
                foreach (var sub in filter.Interleave.Filters)
                {
                    ValidateLabelConstraintsInChain(sub, inChain: false, labelCount: 0);
                }
                return labelCount;

            case RowFilter.FilterOneofCase.Condition:
                if (filter.Condition.PredicateFilter != null)
                    ValidateLabelConstraintsInChain(filter.Condition.PredicateFilter, inChain: false, 0);
                if (filter.Condition.TrueFilter != null)
                    ValidateLabelConstraintsInChain(filter.Condition.TrueFilter, inChain: false, 0);
                if (filter.Condition.FalseFilter != null)
                    ValidateLabelConstraintsInChain(filter.Condition.FalseFilter, inChain: false, 0);
                return labelCount;

            default:
                return labelCount;
        }
    }

    private static readonly Regex LabelRegex = new(@"^[a-z0-9\-]+$", RegexOptions.Compiled);

    private static void ValidateLabel(string label)
    {
        // Ref: "Labels must be at most 15 characters in length"
        if (label.Length > 15)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"ApplyLabelTransformer label exceeds maximum length of 15 characters."));
        }

        // Ref: "and match the RE2 pattern [a-z0-9\\-]+"
        if (!LabelRegex.IsMatch(label))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                $"ApplyLabelTransformer label '{label}' contains invalid characters. Must match [a-z0-9\\-]+."));
        }
    }

    /// <summary>
    /// Anchors a regex pattern to match the entire string (RE2 FullMatch semantics).
    /// Bigtable regex filters match the entire value, not substrings.
    /// Ref: Go emulator uses regexp.Compile("^(?:" + pat + ")$") for all regex filters.
    /// </summary>
    private static string AnchorPattern(string pattern) => $"^(?:{pattern})$";

    #endregion
}
