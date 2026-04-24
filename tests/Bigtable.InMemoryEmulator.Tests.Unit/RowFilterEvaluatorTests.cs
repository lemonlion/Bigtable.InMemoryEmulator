using Bigtable.InMemoryEmulator;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace Bigtable.InMemoryEmulator.Tests;

public class RowFilterEvaluatorTests
{
    private const string Family1 = "cf1";
    private const string Family2 = "cf2";

    private static ByteString B(string s) => ByteString.CopyFromUtf8(s);

    private static CellData Cell(string family, string qualifier, long ts, string value)
        => new()
        {
            Family = family,
            Qualifier = B(qualifier),
            TimestampMicros = ts,
            Value = B(value),
        };

    private static IReadOnlyList<CellData> SampleCells() =>
    [
        Cell(Family1, "name", 3000, "Alice"),
        Cell(Family1, "name", 2000, "Bob"),
        Cell(Family1, "email", 1000, "alice@test.com"),
        Cell(Family2, "score", 1000, "100"),
    ];

    #region PassAll / BlockAll

    [Fact]
    public void PassAll_returns_all_cells()
    {
        var filter = new RowFilter { PassAllFilter = true };
        var result = RowFilterEvaluator.Apply(filter, SampleCells());
        result.Should().HaveCount(4);
    }

    [Fact]
    public void BlockAll_returns_no_cells()
    {
        var filter = new RowFilter { BlockAllFilter = true };
        var result = RowFilterEvaluator.Apply(filter, SampleCells());
        result.Should().BeEmpty();
    }

    #endregion

    #region FamilyNameRegex

    [Fact]
    public void FamilyNameRegex_filters_by_family()
    {
        var filter = new RowFilter { FamilyNameRegexFilter = "cf1" };
        var result = RowFilterEvaluator.Apply(filter, SampleCells());
        result.Should().HaveCount(3);
        result.Should().OnlyContain(c => c.Family == Family1);
    }

    [Fact]
    public void FamilyNameRegex_supports_alternation()
    {
        var filter = new RowFilter { FamilyNameRegexFilter = "cf1|cf2" };
        var result = RowFilterEvaluator.Apply(filter, SampleCells());
        result.Should().HaveCount(4);
    }

    [Fact]
    public void FamilyNameRegex_no_match_returns_empty()
    {
        var filter = new RowFilter { FamilyNameRegexFilter = "cf3" };
        var result = RowFilterEvaluator.Apply(filter, SampleCells());
        result.Should().BeEmpty();
    }

    #endregion

    #region ColumnQualifierRegex

    [Fact]
    public void ColumnQualifierRegex_filters_by_qualifier()
    {
        var filter = new RowFilter { ColumnQualifierRegexFilter = B("name") };
        var result = RowFilterEvaluator.Apply(filter, SampleCells());
        result.Should().HaveCount(2);
        result.Should().OnlyContain(c => c.Qualifier.ToStringUtf8() == "name");
    }

    [Fact]
    public void ColumnQualifierRegex_supports_pattern()
    {
        var filter = new RowFilter { ColumnQualifierRegexFilter = B("na.*") };
        var result = RowFilterEvaluator.Apply(filter, SampleCells());
        result.Should().HaveCount(2);
    }

    #endregion

    #region ValueRegex

    [Fact]
    public void ValueRegex_filters_by_value()
    {
        var filter = new RowFilter { ValueRegexFilter = B("Alice") };
        var result = RowFilterEvaluator.Apply(filter, SampleCells());
        result.Should().HaveCount(1);
        result[0].Value.ToStringUtf8().Should().Be("Alice");
    }

    [Fact]
    public void ValueRegex_supports_pattern()
    {
        var filter = new RowFilter { ValueRegexFilter = B(".*@.*") };
        var result = RowFilterEvaluator.Apply(filter, SampleCells());
        result.Should().HaveCount(1);
        result[0].Qualifier.ToStringUtf8().Should().Be("email");
    }

    #endregion

    #region TimestampRange

    [Fact]
    public void TimestampRange_filters_by_range()
    {
        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                StartTimestampMicros = 2000,
                EndTimestampMicros = 4000,
            }
        };
        var result = RowFilterEvaluator.Apply(filter, SampleCells());
        result.Should().HaveCount(2);
        result.Should().OnlyContain(c => c.TimestampMicros >= 2000 && c.TimestampMicros < 4000);
    }

    [Fact]
    public void TimestampRange_start_inclusive_end_exclusive()
    {
        var filter = new RowFilter
        {
            TimestampRangeFilter = new TimestampRange
            {
                StartTimestampMicros = 1000,
                EndTimestampMicros = 2000,
            }
        };
        var result = RowFilterEvaluator.Apply(filter, SampleCells());
        result.Should().HaveCount(2); // ts=1000 (email + score), NOT ts=2000 (end exclusive)
        result.Should().OnlyContain(c => c.TimestampMicros == 1000);
    }

    #endregion

    #region CellsPerColumnLimit

    [Fact]
    public void CellsPerColumnLimit_limits_versions()
    {
        var filter = new RowFilter { CellsPerColumnLimitFilter = 1 };
        var result = RowFilterEvaluator.Apply(filter, SampleCells());
        // name has 2 versions, limit to 1; email has 1; score has 1 → total 3
        result.Should().HaveCount(3);
        // The kept "name" cell should be the first one (ts=3000, newest)
        var nameCells = result.Where(c => c.Qualifier.ToStringUtf8() == "name").ToList();
        nameCells.Should().HaveCount(1);
        nameCells[0].TimestampMicros.Should().Be(3000);
    }

    #endregion

    #region CellsPerRowLimit

    [Fact]
    public void CellsPerRowLimit_limits_total_cells()
    {
        var filter = new RowFilter { CellsPerRowLimitFilter = 2 };
        var result = RowFilterEvaluator.Apply(filter, SampleCells());
        result.Should().HaveCount(2);
    }

    #endregion

    #region CellsPerRowOffset

    [Fact]
    public void CellsPerRowOffset_skips_cells()
    {
        var filter = new RowFilter { CellsPerRowOffsetFilter = 2 };
        var result = RowFilterEvaluator.Apply(filter, SampleCells());
        result.Should().HaveCount(2);
    }

    #endregion

    #region StripValueTransformer

    [Fact]
    public void StripValue_replaces_values_with_empty()
    {
        var filter = new RowFilter { StripValueTransformer = true };
        var result = RowFilterEvaluator.Apply(filter, SampleCells());
        result.Should().HaveCount(4);
        result.Should().OnlyContain(c => c.Value.IsEmpty);
    }

    #endregion

    #region ApplyLabelTransformer

    [Fact]
    public void ApplyLabel_adds_label_to_cells()
    {
        var filter = new RowFilter { ApplyLabelTransformer = "matched" };
        var result = RowFilterEvaluator.Apply(filter, SampleCells());
        result.Should().HaveCount(4);
        result.Should().OnlyContain(c => c.Labels.Contains("matched"));
    }

    [Fact]
    public void ApplyLabel_exceeding_15_chars_throws_InvalidArgument()
    {
        var filter = new RowFilter { ApplyLabelTransformer = "abcdefghijklmnop" }; // 16 chars
        var act = () => RowFilterEvaluator.Apply(filter, SampleCells());
        act.Should().Throw<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public void ApplyLabel_invalid_chars_throws_InvalidArgument()
    {
        var filter = new RowFilter { ApplyLabelTransformer = "INVALID" };
        var act = () => RowFilterEvaluator.Apply(filter, SampleCells());
        act.Should().Throw<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    #endregion

    #region RowKeyRegex

    [Fact]
    public void RowKeyRegex_matches_row_key()
    {
        var filter = new RowFilter { RowKeyRegexFilter = B("user-.*") };
        var result = RowFilterEvaluator.Apply(filter, SampleCells(), B("user-123"));
        result.Should().HaveCount(4);
    }

    [Fact]
    public void RowKeyRegex_no_match_returns_empty()
    {
        var filter = new RowFilter { RowKeyRegexFilter = B("user-.*") };
        var result = RowFilterEvaluator.Apply(filter, SampleCells(), B("admin-456"));
        result.Should().BeEmpty();
    }

    #endregion

    #region ColumnRange

    [Fact]
    public void ColumnRange_filters_within_range()
    {
        var filter = new RowFilter
        {
            ColumnRangeFilter = new ColumnRange
            {
                FamilyName = Family1,
                StartQualifierClosed = B("email"),
                EndQualifierOpen = B("name"),
            }
        };
        var result = RowFilterEvaluator.Apply(filter, SampleCells());
        result.Should().HaveCount(1);
        result[0].Qualifier.ToStringUtf8().Should().Be("email");
    }

    #endregion

    #region ValueRange

    [Fact]
    public void ValueRange_filters_within_range()
    {
        var filter = new RowFilter
        {
            ValueRangeFilter = new ValueRange
            {
                StartValueClosed = B("A"),
                EndValueOpen = B("C"),
            }
        };
        var result = RowFilterEvaluator.Apply(filter, SampleCells());
        result.Should().HaveCount(2); // "Alice" and "Bob"
    }

    #endregion

    #region Chain

    [Fact]
    public void Chain_applies_filters_sequentially()
    {
        var filter = new RowFilter
        {
            Chain = new RowFilter.Types.Chain
            {
                Filters =
                {
                    new RowFilter { FamilyNameRegexFilter = "cf1" },
                    new RowFilter { ColumnQualifierRegexFilter = B("name") },
                    new RowFilter { CellsPerColumnLimitFilter = 1 },
                }
            }
        };

        var result = RowFilterEvaluator.Apply(filter, SampleCells());
        result.Should().HaveCount(1);
        result[0].Value.ToStringUtf8().Should().Be("Alice");
    }

    [Fact]
    public void Chain_short_circuits_on_empty()
    {
        var filter = new RowFilter
        {
            Chain = new RowFilter.Types.Chain
            {
                Filters =
                {
                    new RowFilter { BlockAllFilter = true },
                    new RowFilter { FamilyNameRegexFilter = "cf1" },
                }
            }
        };

        var result = RowFilterEvaluator.Apply(filter, SampleCells());
        result.Should().BeEmpty();
    }

    #endregion

    #region Interleave

    [Fact]
    public void Interleave_unions_filter_results()
    {
        var filter = new RowFilter
        {
            Interleave = new RowFilter.Types.Interleave
            {
                Filters =
                {
                    new RowFilter { ColumnQualifierRegexFilter = B("name") },
                    new RowFilter { ColumnQualifierRegexFilter = B("score") },
                }
            }
        };

        var result = RowFilterEvaluator.Apply(filter, SampleCells());
        // name has 2 cells + score has 1 cell = 3
        result.Should().HaveCount(3);
    }

    #endregion

    #region Condition

    [Fact]
    public void Condition_applies_true_filter_when_predicate_matches()
    {
        var filter = new RowFilter
        {
            Condition = new RowFilter.Types.Condition
            {
                PredicateFilter = new RowFilter { FamilyNameRegexFilter = "cf2" },
                TrueFilter = new RowFilter { PassAllFilter = true },
                FalseFilter = new RowFilter { BlockAllFilter = true },
            }
        };

        var result = RowFilterEvaluator.Apply(filter, SampleCells());
        result.Should().HaveCount(4); // predicate matched → pass all
    }

    [Fact]
    public void Condition_applies_false_filter_when_predicate_fails()
    {
        var filter = new RowFilter
        {
            Condition = new RowFilter.Types.Condition
            {
                PredicateFilter = new RowFilter { FamilyNameRegexFilter = "cf3" }, // no match
                TrueFilter = new RowFilter { PassAllFilter = true },
                FalseFilter = new RowFilter { BlockAllFilter = true },
            }
        };

        var result = RowFilterEvaluator.Apply(filter, SampleCells());
        result.Should().BeEmpty(); // predicate failed → block all
    }

    #endregion

    #region Validation

    [Fact]
    public void Validate_chain_with_two_labels_throws_InvalidArgument()
    {
        var filter = new RowFilter
        {
            Chain = new RowFilter.Types.Chain
            {
                Filters =
                {
                    new RowFilter { ApplyLabelTransformer = "a" },
                    new RowFilter { ApplyLabelTransformer = "b" },
                }
            }
        };

        var act = () => RowFilterEvaluator.Validate(filter);
        act.Should().Throw<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public void Validate_interleave_with_multiple_labels_is_allowed()
    {
        var filter = new RowFilter
        {
            Interleave = new RowFilter.Types.Interleave
            {
                Filters =
                {
                    new RowFilter { ApplyLabelTransformer = "a" },
                    new RowFilter { ApplyLabelTransformer = "b" },
                }
            }
        };

        var act = () => RowFilterEvaluator.Validate(filter);
        act.Should().NotThrow();
    }

    [Fact]
    public void Null_filter_returns_all_cells()
    {
        var result = RowFilterEvaluator.Apply(null, SampleCells());
        result.Should().HaveCount(4);
    }

    #endregion

    #region Sink

    [Fact]
    public void Sink_true_passes_all_cells()
    {
        // Ref: RowFilter.sink — "Hook for introspection into the RowFilter."
        var filter = new RowFilter { Sink = true };
        var result = RowFilterEvaluator.Apply(filter, SampleCells());
        result.Should().HaveCount(4);
    }

    [Fact]
    public void Sink_false_blocks_all_cells()
    {
        var filter = new RowFilter { Sink = false };
        var result = RowFilterEvaluator.Apply(filter, SampleCells());
        result.Should().BeEmpty();
    }

    #endregion
}
