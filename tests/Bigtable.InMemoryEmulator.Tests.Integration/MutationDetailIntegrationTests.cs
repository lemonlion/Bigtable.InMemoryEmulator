using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Comprehensive mutation integration tests — deletion variants, multi-mutation calls,
/// timestamp semantics, and edge cases that affect parity.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutationDetailIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "mut-detail-tests";
    private const string CF = "cf";
    private const string CF2 = "cf2";

    public MutationDetailIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF, CF2 });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    #region DeleteFromColumn

    [Fact]
    public async Task DeleteFromColumn_removes_specific_column()
    {
        // Ref: "Deletes cells from a column."
        var rk = new BigtableByteString("dfc-col");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromColumn(CF, "a"));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families[0].Columns.Should().ContainSingle();
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("b");
    }

    [Fact]
    public async Task DeleteFromColumn_with_time_range()
    {
        // Ref: TimestampRange in DeleteFromColumn — "Delete cells in the column within specified time range"
        var rk = new BigtableByteString("dfc-tr");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "a", "v3", new BigtableVersion(3000)));
        // Delete versions in range [2000ms, 3000ms) → deletes only v2
        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromColumn(CF, "a",
            new BigtableVersionRange(new BigtableVersion(2000), new BigtableVersion(3000))));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        var cells = row!.Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2);
        cells.Select(c => c.Value.ToStringUtf8()).Should().Contain("v1");
        cells.Select(c => c.Value.ToStringUtf8()).Should().Contain("v3");
    }

    [Fact]
    public async Task DeleteFromColumn_all_versions_makes_row_invisible()
    {
        var rk = new BigtableByteString("dfc-all");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "only", "val", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromColumn(CF, "only"));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().BeNull();
    }

    #endregion

    #region DeleteFromFamily

    [Fact]
    public async Task DeleteFromFamily_removes_all_columns_in_family()
    {
        // Ref: "Deletes cells from an entire family."
        var rk = new BigtableByteString("dff-fam");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "x", "v3", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromFamily(CF));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.Should().ContainSingle();
        row.Families[0].Name.Should().Be(CF2);
    }

    [Fact]
    public async Task DeleteFromFamily_all_families_makes_row_invisible()
    {
        var rk = new BigtableByteString("dff-all");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "b", "v2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromFamily(CF));
        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromFamily(CF2));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().BeNull();
    }

    #endregion

    #region Multiple mutations in single MutateRow call

    [Fact]
    public async Task MutateRow_multiple_mutations_are_atomic()
    {
        // Ref: "Atomically apply the given mutations to the specified row."
        var rk = new BigtableByteString("multi-mut");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "v3", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        var allCells = row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells).ToList();
        allCells.Should().HaveCount(3);
    }

    [Fact]
    public async Task MutateRow_set_then_delete_in_same_call()
    {
        var rk = new BigtableByteString("set-del");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)));
        // Set new value and delete old in same call
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(2000)),
            Mutations.DeleteFromColumn(CF, "a"));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families[0].Columns.Should().ContainSingle();
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("b");
    }

    #endregion

    #region Timestamp semantics

    [Fact]
    public async Task SetCell_same_timestamp_overwrites()
    {
        // Ref: "Cells are uniquely identified by (row_key, column_family, column_qualifier, timestamp)"
        var rk = new BigtableByteString("ts-ow");
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a", "first", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a", "second", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task Cells_ordered_by_timestamp_descending()
    {
        // Ref: "Cells are listed in reverse chronological order."
        var rk = new BigtableByteString("ts-order");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", "oldest", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "middle", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "a", "newest", new BigtableVersion(3000)));
        var row = await Client.ReadRowAsync(TN, rk);
        var values = row!.Families[0].Columns[0].Cells.Select(c => c.Value.ToStringUtf8()).ToList();
        values.Should().Equal("newest", "middle", "oldest");
    }

    [Fact]
    public async Task Columns_ordered_lexicographically()
    {
        // Ref: Columns within a family are returned in qualifier-ordered manner
        var rk = new BigtableByteString("col-order");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, rk);
        var qualifiers = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        qualifiers.Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task Families_ordered_lexicographically()
    {
        var rk = new BigtableByteString("fam-order");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF2, "x", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "v2", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.Select(f => f.Name).Should().Equal(CF, CF2);
    }

    #endregion

    #region Large values

    [Fact]
    public async Task SetCell_with_empty_value()
    {
        var rk = new BigtableByteString("empty-val");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", ByteString.Empty, new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.Should().BeEquivalentTo(ByteString.Empty);
    }

    [Fact]
    public async Task SetCell_with_binary_value()
    {
        var rk = new BigtableByteString("binary-val");
        var binaryData = new byte[] { 0x00, 0x01, 0xFF, 0xFE, 0x00, 0x80 };
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", ByteString.CopyFrom(binaryData), new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells[0].Value.ToByteArray().Should().Equal(binaryData);
    }

    [Fact]
    public async Task SetCell_with_large_value()
    {
        var rk = new BigtableByteString("large-val");
        var largeValue = new byte[10 * 1024]; // 10 KiB
        Array.Fill(largeValue, (byte)0x42);
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "a", new BigtableByteString(largeValue), new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Cells[0].Value.Length.Should().Be(largeValue.Length);
    }

    #endregion

    #region Row key variations

    [Fact]
    public async Task RowKey_with_binary_bytes()
    {
        var binaryKey = new byte[] { 0x00, 0x01, 0xFF };
        var rk = new BigtableByteString(binaryKey);
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a", "val", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Key.ToByteArray().Should().Equal(binaryKey);
    }

    [Fact]
    public async Task RowKey_with_unicode()
    {
        var rk = new BigtableByteString("日本語キー");
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a", "val", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Key.ToStringUtf8().Should().Be("日本語キー");
    }

    [Fact]
    public async Task RowKey_max_size_4KiB_succeeds()
    {
        // Boundary test: exactly 4096 bytes should succeed
        var rk = new BigtableByteString(new byte[4096]);
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF, "a", "val", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
    }

    #endregion

    #region Qualifier edge cases

    [Fact]
    public async Task Empty_qualifier_is_valid()
    {
        // Ref: Column qualifiers can be empty in Bigtable
        var rk = new BigtableByteString("empty-qual");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, ByteString.Empty, ByteString.CopyFromUtf8("val"), new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Qualifier.Should().BeEquivalentTo(ByteString.Empty);
    }

    [Fact]
    public async Task Binary_qualifier()
    {
        var rk = new BigtableByteString("bin-qual");
        var qual = ByteString.CopyFrom(new byte[] { 0x00, 0xFF, 0x80 });
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, qual, ByteString.CopyFromUtf8("val"), new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Qualifier.Should().BeEquivalentTo(qual);
    }

    #endregion
}
