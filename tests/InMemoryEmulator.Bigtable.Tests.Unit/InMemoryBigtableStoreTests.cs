using InMemoryEmulator.Bigtable;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace InMemoryEmulator.Bigtable.Tests;

public class InMemoryBigtableStoreTests : IDisposable
{
    private readonly InMemoryBigtableStore _store = new();
    private const string TableName = "test-table";
    private const string Family = "cf1";
    private const string Family2 = "cf2";

    public InMemoryBigtableStoreTests()
    {
        _store.CreateTable(TableName, [Family, Family2]);
    }

    public void Dispose() => _store.Dispose();

    #region Table Management

    [Fact]
    public void CreateTable_registers_table()
    {
        _store.TableExists(TableName).Should().BeTrue();
    }

    [Fact]
    public void CreateTable_duplicate_throws_AlreadyExists()
    {
        var act = () => _store.CreateTable(TableName, [Family]);
        act.Should().Throw<RpcException>()
            .Where(e => e.StatusCode == StatusCode.AlreadyExists);
    }

    [Fact]
    public void GetTable_nonexistent_throws_NotFound()
    {
        var act = () => _store.GetTable("no-such-table");
        act.Should().Throw<RpcException>()
            .Where(e => e.StatusCode == StatusCode.NotFound);
    }

    [Fact]
    public void DeleteTable_removes_table()
    {
        _store.DeleteTable(TableName);
        _store.TableExists(TableName).Should().BeFalse();
    }

    [Fact]
    public void DeleteTable_nonexistent_throws_NotFound()
    {
        var act = () => _store.DeleteTable("no-such-table");
        act.Should().Throw<RpcException>()
            .Where(e => e.StatusCode == StatusCode.NotFound);
    }

    [Fact]
    public void ListTables_returns_all_tables()
    {
        _store.CreateTable("second-table", ["cf"]);
        _store.ListTables().Should().BeEquivalentTo([TableName, "second-table"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a family with spaces")]
    [InlineData("family!")]
    [InlineData("family@name")]
    public void CreateTable_invalid_family_name_throws_InvalidArgument(string badName)
    {
        var act = () => _store.CreateTable("bad-family-table", [badName]);
        act.Should().Throw<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public void CreateTable_family_name_exceeding_64_chars_throws_InvalidArgument()
    {
        var longName = new string('a', 65);
        var act = () => _store.CreateTable("long-family-table", [longName]);
        act.Should().Throw<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("cf1")]
    [InlineData("my_family")]
    [InlineData("my-family")]
    [InlineData("my.family")]
    [InlineData("_private")]
    public void CreateTable_valid_family_names_succeed(string validName)
    {
        _store.CreateTable($"table-{validName}", [validName]);
        _store.TableExists($"table-{validName}").Should().BeTrue();
    }

    #endregion

    #region MutateRow — SetCell

    [Fact]
    public void MutateRow_SetCell_stores_value()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("col1");
        var value = ByteString.CopyFromUtf8("hello");

        table.MutateRow(rowKey, [NewSetCell(Family, qualifier, 1000, value)]);

        var row = table.GetRow(rowKey);
        row.Should().NotBeNull();
        var cells = row!.GetCells();
        cells.Should().HaveCount(1);
        cells[0].Family.Should().Be(Family);
        cells[0].Qualifier.ToStringUtf8().Should().Be(qualifier.ToStringUtf8());
        cells[0].TimestampMicros.Should().Be(1000);
        cells[0].Value.ToStringUtf8().Should().Be(value.ToStringUtf8());
    }

    [Fact]
    public void MutateRow_SetCell_with_server_timestamp_assigns_timestamp()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("col1");
        var value = ByteString.CopyFromUtf8("hello");

        // timestamp_micros = -1 means server-assigned
        table.MutateRow(rowKey, [NewSetCell(Family, qualifier, -1, value)]);

        var row = table.GetRow(rowKey);
        row.Should().NotBeNull();
        var cells = row!.GetCells();
        cells.Should().HaveCount(1);
        // Server timestamp should be recent and ms-aligned
        cells[0].TimestampMicros.Should().BeGreaterThan(0);
        (cells[0].TimestampMicros % 1000).Should().Be(0);
    }

    [Fact]
    public void MutateRow_SetCell_with_non_ms_aligned_timestamp_throws_InvalidArgument()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("col1");
        var value = ByteString.CopyFromUtf8("hello");

        var act = () => table.MutateRow(rowKey, [NewSetCell(Family, qualifier, 1001, value)]);

        act.Should().Throw<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public void MutateRow_SetCell_with_timestamp_zero_is_valid()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("col1");
        var value = ByteString.CopyFromUtf8("hello");

        table.MutateRow(rowKey, [NewSetCell(Family, qualifier, 0, value)]);

        var cells = table.GetRow(rowKey)!.GetCells();
        cells[0].TimestampMicros.Should().Be(0);
    }

    [Fact]
    public void MutateRow_SetCell_overwrites_same_cell()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("col1");

        table.MutateRow(rowKey, [NewSetCell(Family, qualifier, 1000, ByteString.CopyFromUtf8("v1"))]);
        table.MutateRow(rowKey, [NewSetCell(Family, qualifier, 1000, ByteString.CopyFromUtf8("v2"))]);

        var cells = table.GetRow(rowKey)!.GetCells();
        cells.Should().HaveCount(1);
        cells[0].Value.ToStringUtf8().Should().Be("v2");
    }

    [Fact]
    public void MutateRow_SetCell_different_timestamps_create_multiple_versions()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("col1");

        table.MutateRow(rowKey, [NewSetCell(Family, qualifier, 1000, ByteString.CopyFromUtf8("v1"))]);
        table.MutateRow(rowKey, [NewSetCell(Family, qualifier, 2000, ByteString.CopyFromUtf8("v2"))]);

        var cells = table.GetRow(rowKey)!.GetCells();
        cells.Should().HaveCount(2);
        // Timestamp descending: newest first
        cells[0].TimestampMicros.Should().Be(2000);
        cells[1].TimestampMicros.Should().Be(1000);
    }

    [Fact]
    public void MutateRow_SetCell_nonexistent_family_throws_InvalidArgument()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("col1");
        var value = ByteString.CopyFromUtf8("hello");

        var act = () => table.MutateRow(rowKey, [NewSetCell("no_such_family", qualifier, 1000, value)]);
        act.Should().Throw<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    #endregion

    #region MutateRow — DeleteFromColumn

    [Fact]
    public void MutateRow_DeleteFromColumn_removes_matching_cells()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("col1");

        table.MutateRow(rowKey, [
            NewSetCell(Family, qualifier, 1000, ByteString.CopyFromUtf8("v1")),
            NewSetCell(Family, qualifier, 2000, ByteString.CopyFromUtf8("v2")),
            NewSetCell(Family, qualifier, 3000, ByteString.CopyFromUtf8("v3")),
        ]);

        // Delete cells with timestamp in [1000, 3000)
        table.MutateRow(rowKey, [NewDeleteFromColumn(Family, qualifier, 1000, 3000)]);

        var cells = table.GetRow(rowKey)!.GetCells();
        cells.Should().HaveCount(1);
        cells[0].TimestampMicros.Should().Be(3000); // Only the latest survives
    }

    [Fact]
    public void MutateRow_DeleteFromColumn_without_range_deletes_all_versions()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("col1");

        table.MutateRow(rowKey, [
            NewSetCell(Family, qualifier, 1000, ByteString.CopyFromUtf8("v1")),
            NewSetCell(Family, qualifier, 2000, ByteString.CopyFromUtf8("v2")),
        ]);

        table.MutateRow(rowKey, [NewDeleteFromColumn(Family, qualifier)]);

        var row = table.GetRow(rowKey);
        // Row should be gone (or empty) since all cells were deleted
        row.Should().BeNull();
    }

    #endregion

    #region MutateRow — DeleteFromFamily

    [Fact]
    public void MutateRow_DeleteFromFamily_removes_all_cells_in_family()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");

        table.MutateRow(rowKey, [
            NewSetCell(Family, ByteString.CopyFromUtf8("col1"), 1000, ByteString.CopyFromUtf8("v1")),
            NewSetCell(Family, ByteString.CopyFromUtf8("col2"), 1000, ByteString.CopyFromUtf8("v2")),
            NewSetCell(Family2, ByteString.CopyFromUtf8("col1"), 1000, ByteString.CopyFromUtf8("v3")),
        ]);

        table.MutateRow(rowKey, [NewDeleteFromFamily(Family)]);

        var cells = table.GetRow(rowKey)!.GetCells();
        cells.Should().HaveCount(1);
        cells[0].Family.Should().Be(Family2);
    }

    #endregion

    #region MutateRow — DeleteFromRow

    [Fact]
    public void MutateRow_DeleteFromRow_removes_all_cells()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");

        table.MutateRow(rowKey, [
            NewSetCell(Family, ByteString.CopyFromUtf8("col1"), 1000, ByteString.CopyFromUtf8("v1")),
            NewSetCell(Family2, ByteString.CopyFromUtf8("col2"), 1000, ByteString.CopyFromUtf8("v2")),
        ]);

        table.MutateRow(rowKey, [new Mutation { DeleteFromRow = new Mutation.Types.DeleteFromRow() }]);

        table.GetRow(rowKey).Should().BeNull();
    }

    #endregion

    #region MutateRow — Validation

    [Fact]
    public void MutateRow_empty_row_key_throws_InvalidArgument()
    {
        var table = _store.GetTable(TableName);

        var act = () => table.MutateRow(ByteString.Empty, [NewSetCell(Family, ByteString.CopyFromUtf8("col1"), 1000, ByteString.CopyFromUtf8("v1"))]);
        act.Should().Throw<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public void MutateRow_row_key_exceeding_4KiB_throws_InvalidArgument()
    {
        var table = _store.GetTable(TableName);
        var largeKey = ByteString.CopyFrom(new byte[4097]);

        var act = () => table.MutateRow(largeKey, [NewSetCell(Family, ByteString.CopyFromUtf8("col1"), 1000, ByteString.CopyFromUtf8("v1"))]);
        act.Should().Throw<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public void MutateRow_empty_mutations_throws_InvalidArgument()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");

        var act = () => table.MutateRow(rowKey, Array.Empty<Mutation>());
        act.Should().Throw<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    [Fact]
    public void MutateRow_multiple_mutations_are_atomic()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");

        table.MutateRow(rowKey, [
            NewSetCell(Family, ByteString.CopyFromUtf8("col1"), 1000, ByteString.CopyFromUtf8("v1")),
            NewSetCell(Family, ByteString.CopyFromUtf8("col2"), 1000, ByteString.CopyFromUtf8("v2")),
            NewSetCell(Family2, ByteString.CopyFromUtf8("col3"), 1000, ByteString.CopyFromUtf8("v3")),
        ]);

        var cells = table.GetRow(rowKey)!.GetCells();
        cells.Should().HaveCount(3);
    }

    #endregion

    #region MutateRows (Batch)

    [Fact]
    public void MutateRows_applies_each_entry_independently()
    {
        var table = _store.GetTable(TableName);

        var entries = new List<MutateRowsRequest.Types.Entry>
        {
            new()
            {
                RowKey = ByteString.CopyFromUtf8("row1"),
                Mutations = { NewSetCell(Family, ByteString.CopyFromUtf8("col1"), 1000, ByteString.CopyFromUtf8("v1")) }
            },
            new()
            {
                RowKey = ByteString.CopyFromUtf8("row2"),
                Mutations = { NewSetCell(Family, ByteString.CopyFromUtf8("col1"), 1000, ByteString.CopyFromUtf8("v2")) }
            },
        };

        var results = table.MutateRows(entries);
        results.Should().HaveCount(2);
        results.All(r => r.Status.StatusCode == StatusCode.OK).Should().BeTrue();

        table.GetRow(ByteString.CopyFromUtf8("row1")).Should().NotBeNull();
        table.GetRow(ByteString.CopyFromUtf8("row2")).Should().NotBeNull();
    }

    [Fact]
    public void MutateRows_reports_per_entry_errors()
    {
        var table = _store.GetTable(TableName);

        var entries = new List<MutateRowsRequest.Types.Entry>
        {
            new()
            {
                RowKey = ByteString.CopyFromUtf8("row1"),
                Mutations = { NewSetCell(Family, ByteString.CopyFromUtf8("col1"), 1000, ByteString.CopyFromUtf8("v1")) }
            },
            new()
            {
                RowKey = ByteString.Empty, // Invalid!
                Mutations = { NewSetCell(Family, ByteString.CopyFromUtf8("col1"), 1000, ByteString.CopyFromUtf8("v2")) }
            },
        };

        var results = table.MutateRows(entries);
        results.Should().HaveCount(2);
        results[0].Status.StatusCode.Should().Be(StatusCode.OK);
        results[1].Status.StatusCode.Should().Be(StatusCode.InvalidArgument);

        // First row should still be written
        table.GetRow(ByteString.CopyFromUtf8("row1")).Should().NotBeNull();
    }

    #endregion

    #region ReadRows

    [Fact]
    public void ReadRows_returns_all_rows_in_lexicographic_order()
    {
        var table = _store.GetTable(TableName);

        table.MutateRow(ByteString.CopyFromUtf8("b"), [NewSetCell(Family, ByteString.CopyFromUtf8("c"), 1000, ByteString.CopyFromUtf8("v"))]);
        table.MutateRow(ByteString.CopyFromUtf8("a"), [NewSetCell(Family, ByteString.CopyFromUtf8("c"), 1000, ByteString.CopyFromUtf8("v"))]);
        table.MutateRow(ByteString.CopyFromUtf8("c"), [NewSetCell(Family, ByteString.CopyFromUtf8("c"), 1000, ByteString.CopyFromUtf8("v"))]);

        var rows = table.ReadRows().ToList();

        rows.Should().HaveCount(3);
        rows[0].Key.ToStringUtf8().Should().Be("a");
        rows[1].Key.ToStringUtf8().Should().Be("b");
        rows[2].Key.ToStringUtf8().Should().Be("c");
    }

    [Fact]
    public void ReadRows_with_specific_keys_returns_only_matching()
    {
        var table = _store.GetTable(TableName);

        table.MutateRow(ByteString.CopyFromUtf8("a"), [NewSetCell(Family, ByteString.CopyFromUtf8("c"), 1000, ByteString.CopyFromUtf8("v"))]);
        table.MutateRow(ByteString.CopyFromUtf8("b"), [NewSetCell(Family, ByteString.CopyFromUtf8("c"), 1000, ByteString.CopyFromUtf8("v"))]);
        table.MutateRow(ByteString.CopyFromUtf8("c"), [NewSetCell(Family, ByteString.CopyFromUtf8("c"), 1000, ByteString.CopyFromUtf8("v"))]);

        var rows = table.ReadRows(
            rowKeys: [ByteString.CopyFromUtf8("a"), ByteString.CopyFromUtf8("c")]).ToList();

        rows.Should().HaveCount(2);
        rows[0].Key.ToStringUtf8().Should().Be("a");
        rows[1].Key.ToStringUtf8().Should().Be("c");
    }

    [Fact]
    public void ReadRows_with_row_range_returns_matching()
    {
        var table = _store.GetTable(TableName);

        table.MutateRow(ByteString.CopyFromUtf8("a"), [NewSetCell(Family, ByteString.CopyFromUtf8("c"), 1000, ByteString.CopyFromUtf8("v"))]);
        table.MutateRow(ByteString.CopyFromUtf8("b"), [NewSetCell(Family, ByteString.CopyFromUtf8("c"), 1000, ByteString.CopyFromUtf8("v"))]);
        table.MutateRow(ByteString.CopyFromUtf8("c"), [NewSetCell(Family, ByteString.CopyFromUtf8("c"), 1000, ByteString.CopyFromUtf8("v"))]);
        table.MutateRow(ByteString.CopyFromUtf8("d"), [NewSetCell(Family, ByteString.CopyFromUtf8("c"), 1000, ByteString.CopyFromUtf8("v"))]);

        // Range [b, d) — includes b, c but not d
        var range = new RowRange
        {
            StartKey = ByteString.CopyFromUtf8("b"),
            StartKeyCase = RowRange.StartKeyOneofCase.StartKeyClosed,
            EndKey = ByteString.CopyFromUtf8("d"),
            EndKeyCase = RowRange.EndKeyOneofCase.EndKeyOpen,
        };

        var rows = table.ReadRows(rowRanges: [range]).ToList();
        rows.Should().HaveCount(2);
        rows[0].Key.ToStringUtf8().Should().Be("b");
        rows[1].Key.ToStringUtf8().Should().Be("c");
    }

    [Fact]
    public void ReadRows_returns_all_rows_without_limit()
    {
        var table = _store.GetTable(TableName);

        for (int i = 0; i < 10; i++)
        {
            table.MutateRow(ByteString.CopyFromUtf8($"row{i:D2}"), [NewSetCell(Family, ByteString.CopyFromUtf8("c"), 1000, ByteString.CopyFromUtf8("v"))]);
        }

        var rows = table.ReadRows().ToList();
        rows.Should().HaveCount(10);
    }

    [Fact]
    public void ReadRows_reversed_returns_descending_order()
    {
        var table = _store.GetTable(TableName);

        table.MutateRow(ByteString.CopyFromUtf8("a"), [NewSetCell(Family, ByteString.CopyFromUtf8("c"), 1000, ByteString.CopyFromUtf8("v"))]);
        table.MutateRow(ByteString.CopyFromUtf8("b"), [NewSetCell(Family, ByteString.CopyFromUtf8("c"), 1000, ByteString.CopyFromUtf8("v"))]);
        table.MutateRow(ByteString.CopyFromUtf8("c"), [NewSetCell(Family, ByteString.CopyFromUtf8("c"), 1000, ByteString.CopyFromUtf8("v"))]);

        var rows = table.ReadRows(reversed: true).ToList();
        rows.Should().HaveCount(3);
        rows[0].Key.ToStringUtf8().Should().Be("c");
        rows[1].Key.ToStringUtf8().Should().Be("b");
        rows[2].Key.ToStringUtf8().Should().Be("a");
    }

    [Fact]
    public void ReadRows_empty_table_returns_empty()
    {
        var table = _store.GetTable(TableName);
        table.ReadRows().Should().BeEmpty();
    }

    #endregion

    #region CheckAndMutateRow

    [Fact]
    public void CheckAndMutateRow_applies_true_mutations_when_predicate_matches()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("col1");

        table.MutateRow(rowKey, [NewSetCell(Family, qualifier, 1000, ByteString.CopyFromUtf8("existing"))]);

        bool result = table.CheckAndMutateRow(
            rowKey,
            predicateFilter: row => row.GetCells().Any(),
            trueMutations: [NewSetCell(Family, qualifier, 2000, ByteString.CopyFromUtf8("updated"))],
            falseMutations: null);

        result.Should().BeTrue();
        var cells = table.GetRow(rowKey)!.GetCells();
        cells.Should().HaveCount(2); // Both versions
        cells[0].Value.ToStringUtf8().Should().Be("updated");
    }

    [Fact]
    public void CheckAndMutateRow_applies_false_mutations_when_predicate_fails()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("col1");

        // Row doesn't exist, so predicate returns false
        bool result = table.CheckAndMutateRow(
            rowKey,
            predicateFilter: row => row.GetCells().Any(),
            trueMutations: null,
            falseMutations: [NewSetCell(Family, qualifier, 1000, ByteString.CopyFromUtf8("created"))]);

        result.Should().BeFalse();
        var cells = table.GetRow(rowKey)!.GetCells();
        cells.Should().HaveCount(1);
        cells[0].Value.ToStringUtf8().Should().Be("created");
    }

    #endregion

    #region ReadModifyWriteRow

    [Fact]
    public void ReadModifyWriteRow_increment_creates_new_cell_from_zero()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("counter");

        var result = table.ReadModifyWriteRow(rowKey, [
            new ReadModifyWriteRule
            {
                FamilyName = Family,
                ColumnQualifier = qualifier,
                IncrementAmount = 42,
            }
        ]);

        result.Should().HaveCount(1);
        ReadBigEndianInt64(result[0].Value).Should().Be(42);
    }

    [Fact]
    public void ReadModifyWriteRow_increment_adds_to_existing_value()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("counter");

        // Set initial value = 10
        table.MutateRow(rowKey, [NewSetCell(Family, qualifier, 1000, WriteBigEndianInt64(10))]);

        var result = table.ReadModifyWriteRow(rowKey, [
            new ReadModifyWriteRule
            {
                FamilyName = Family,
                ColumnQualifier = qualifier,
                IncrementAmount = 5,
            }
        ]);

        ReadBigEndianInt64(result[0].Value).Should().Be(15);
    }

    [Fact]
    public void ReadModifyWriteRow_append_concatenates_bytes()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("data");

        table.MutateRow(rowKey, [NewSetCell(Family, qualifier, 1000, ByteString.CopyFromUtf8("hello"))]);

        var result = table.ReadModifyWriteRow(rowKey, [
            new ReadModifyWriteRule
            {
                FamilyName = Family,
                ColumnQualifier = qualifier,
                AppendValue = ByteString.CopyFromUtf8(" world"),
            }
        ]);

        result[0].Value.ToStringUtf8().Should().Be("hello world");
    }

    [Fact]
    public void ReadModifyWriteRow_append_to_empty_cell_creates_value()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("data");

        var result = table.ReadModifyWriteRow(rowKey, [
            new ReadModifyWriteRule
            {
                FamilyName = Family,
                ColumnQualifier = qualifier,
                AppendValue = ByteString.CopyFromUtf8("hello"),
            }
        ]);

        result[0].Value.ToStringUtf8().Should().Be("hello");
    }

    [Fact]
    public void ReadModifyWriteRow_empty_rules_throws_InvalidArgument()
    {
        var table = _store.GetTable(TableName);

        var act = () => table.ReadModifyWriteRow(ByteString.CopyFromUtf8("row1"), []);
        act.Should().Throw<RpcException>()
            .Where(e => e.StatusCode == StatusCode.InvalidArgument);
    }

    #endregion

    #region Row Cell Ordering

    [Fact]
    public void Cells_are_ordered_by_family_then_qualifier_then_timestamp_desc()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");

        table.MutateRow(rowKey, [
            NewSetCell(Family2, ByteString.CopyFromUtf8("b"), 1000, ByteString.CopyFromUtf8("v")),
            NewSetCell(Family, ByteString.CopyFromUtf8("a"), 2000, ByteString.CopyFromUtf8("v")),
            NewSetCell(Family, ByteString.CopyFromUtf8("a"), 1000, ByteString.CopyFromUtf8("v")),
            NewSetCell(Family, ByteString.CopyFromUtf8("b"), 3000, ByteString.CopyFromUtf8("v")),
        ]);

        var cells = table.GetRow(rowKey)!.GetCells();
        cells.Should().HaveCount(4);

        // cf1:a ts=2000
        cells[0].Family.Should().Be(Family);
        cells[0].Qualifier.ToStringUtf8().Should().Be("a");
        cells[0].TimestampMicros.Should().Be(2000);

        // cf1:a ts=1000
        cells[1].Family.Should().Be(Family);
        cells[1].Qualifier.ToStringUtf8().Should().Be("a");
        cells[1].TimestampMicros.Should().Be(1000);

        // cf1:b ts=3000
        cells[2].Family.Should().Be(Family);
        cells[2].Qualifier.ToStringUtf8().Should().Be("b");
        cells[2].TimestampMicros.Should().Be(3000);

        // cf2:b ts=1000
        cells[3].Family.Should().Be(Family2);
        cells[3].Qualifier.ToStringUtf8().Should().Be("b");
        cells[3].TimestampMicros.Should().Be(1000);
    }

    #endregion

    #region ClearRows

    [Fact]
    public void ClearRows_removes_all_rows()
    {
        var table = _store.GetTable(TableName);

        table.MutateRow(ByteString.CopyFromUtf8("row1"), [NewSetCell(Family, ByteString.CopyFromUtf8("c"), 1000, ByteString.CopyFromUtf8("v"))]);
        table.MutateRow(ByteString.CopyFromUtf8("row2"), [NewSetCell(Family, ByteString.CopyFromUtf8("c"), 1000, ByteString.CopyFromUtf8("v"))]);

        table.ClearRows();

        table.RowCount.Should().Be(0);
        table.ReadRows().Should().BeEmpty();
    }

    #endregion

    #region Concurrency

    [Fact]
    public void Concurrent_mutations_to_different_rows_are_safe()
    {
        var table = _store.GetTable(TableName);

        Parallel.For(0, 100, i =>
        {
            var rowKey = ByteString.CopyFromUtf8($"row{i:D4}");
            table.MutateRow(rowKey, [NewSetCell(Family, ByteString.CopyFromUtf8("col"), 1000, ByteString.CopyFromUtf8($"v{i}"))]);
        });

        table.RowCount.Should().Be(100);
    }

    [Fact]
    public void Concurrent_mutations_to_same_row_are_safe()
    {
        var table = _store.GetTable(TableName);
        var rowKey = ByteString.CopyFromUtf8("row1");

        Parallel.For(0, 100, i =>
        {
            var ts = (long)(i + 1) * 1000;
            table.MutateRow(rowKey, [NewSetCell(Family, ByteString.CopyFromUtf8("col"), ts, ByteString.CopyFromUtf8($"v{i}"))]);
        });

        var cells = table.GetRow(rowKey)!.GetCells();
        cells.Should().HaveCount(100);
    }

    #endregion

    #region GC Rules

    [Fact]
    public void GC_MaxNumVersions_evicts_oldest_on_write()
    {
        var gcRules = new Dictionary<string, Google.Cloud.Bigtable.Admin.V2.GcRule?>
        {
            [Family] = new Google.Cloud.Bigtable.Admin.V2.GcRule { MaxNumVersions = 2 }
        };
        _store.CreateTable("gc-table", [Family], gcRules);
        var table = _store.GetTable("gc-table");
        var rowKey = ByteString.CopyFromUtf8("row1");
        var qualifier = ByteString.CopyFromUtf8("col1");

        table.MutateRow(rowKey, [NewSetCell(Family, qualifier, 1000, ByteString.CopyFromUtf8("v1"))]);
        table.MutateRow(rowKey, [NewSetCell(Family, qualifier, 2000, ByteString.CopyFromUtf8("v2"))]);
        table.MutateRow(rowKey, [NewSetCell(Family, qualifier, 3000, ByteString.CopyFromUtf8("v3"))]);

        var cells = table.GetRow(rowKey)!.GetCells();
        cells.Should().HaveCount(2);
        cells[0].TimestampMicros.Should().Be(3000);
        cells[1].TimestampMicros.Should().Be(2000);
    }

    #endregion

    #region Helpers

    private static Mutation NewSetCell(string family, ByteString qualifier, long timestampMicros, ByteString value)
    {
        return new Mutation
        {
            SetCell = new Mutation.Types.SetCell
            {
                FamilyName = family,
                ColumnQualifier = qualifier,
                TimestampMicros = timestampMicros,
                Value = value,
            }
        };
    }

    private static Mutation NewDeleteFromColumn(string family, ByteString qualifier, long? start = null, long? end = null)
    {
        var delete = new Mutation.Types.DeleteFromColumn
        {
            FamilyName = family,
            ColumnQualifier = qualifier,
        };

        if (start.HasValue || end.HasValue)
        {
            delete.TimeRange = new TimestampRange();
            if (start.HasValue) delete.TimeRange.StartTimestampMicros = start.Value;
            if (end.HasValue) delete.TimeRange.EndTimestampMicros = end.Value;
        }

        return new Mutation { DeleteFromColumn = delete };
    }

    private static Mutation NewDeleteFromFamily(string family)
    {
        return new Mutation
        {
            DeleteFromFamily = new Mutation.Types.DeleteFromFamily { FamilyName = family }
        };
    }

    private static long ReadBigEndianInt64(ByteString value)
    {
        if (value.IsEmpty) return 0;
        var bytes = new byte[8];
        value.Span.CopyTo(bytes.AsSpan(8 - Math.Min(8, value.Length)));
        return System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(bytes);
    }

    private static ByteString WriteBigEndianInt64(long value)
    {
        var bytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return ByteString.CopyFrom(bytes);
    }

    #endregion
}
