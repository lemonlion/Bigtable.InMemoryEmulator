using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for multi-family interactions — writing/reading across families,
/// family-scoped operations, and cross-family consistency.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#family
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MultiFamilyInteractionTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "multi-fam";
    private const string CF1 = "cf1";
    private const string CF2 = "cf2";
    private const string CF3 = "cf3";

    public MultiFamilyInteractionTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF1, CF2, CF3 });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task Write_to_all_three_families()
    {
        var rk = new BigtableByteString("mf-3fam");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF1, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF3, "c", "3", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.Should().HaveCount(3);
    }

    [Fact]
    public async Task Families_sorted_by_name()
    {
        var rk = new BigtableByteString("mf-sorted");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF3, "c", "3", new BigtableVersion(1000)),
            Mutations.SetCell(CF1, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "b", "2", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        var names = row!.Families.Select(f => f.Name).ToList();
        names.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task FamilyNameRegex_filters_to_one_family()
    {
        var rk = new BigtableByteString("mf-famfilt");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF1, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "b", "2", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("mf-famfilt") } },
            Filter = RowFilters.FamilyNameRegex(CF1)
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows[0].Families.Should().HaveCount(1);
        rows[0].Families[0].Name.Should().Be(CF1);
    }

    [Fact]
    public async Task FamilyNameRegex_pattern_matches_multiple()
    {
        var rk = new BigtableByteString("mf-famregex");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF1, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF3, "c", "3", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("mf-famregex") } },
            Filter = RowFilters.FamilyNameRegex("cf[12]")
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows[0].Families.Select(f => f.Name).Should().BeEquivalentTo(new[] { CF1, CF2 });
    }

    [Fact]
    public async Task DeleteFromFamily_removes_only_target_family()
    {
        var rk = new BigtableByteString("mf-delfam");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF1, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF3, "c", "3", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromFamily(CF2));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.Select(f => f.Name).Should().BeEquivalentTo(new[] { CF1, CF3 });
    }

    [Fact]
    public async Task Same_column_name_in_different_families()
    {
        var rk = new BigtableByteString("mf-samecol");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF1, "col", "from-cf1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "col", "from-cf2", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.Should().HaveCount(2);

        var cf1Val = row.Families.First(f => f.Name == CF1).Columns[0].Cells[0].Value.ToStringUtf8();
        var cf2Val = row.Families.First(f => f.Name == CF2).Columns[0].Cells[0].Value.ToStringUtf8();
        cf1Val.Should().Be("from-cf1");
        cf2Val.Should().Be("from-cf2");
    }

    [Fact]
    public async Task Delete_from_one_family_preserves_same_column_in_other()
    {
        var rk = new BigtableByteString("mf-deliso");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF1, "col", "keep", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "col", "remove", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromFamily(CF2));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.Should().HaveCount(1);
        row.Families[0].Name.Should().Be(CF1);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("keep");
    }

    [Fact]
    public async Task ReadModifyWrite_across_families()
    {
        var rk = new BigtableByteString("mf-rmw");

        var resp = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF1, "log", "event1"),
            ReadModifyWriteRules.Increment(CF2, "count", 1));

        var families = resp.Row.Families.Select(f => f.Name).ToList();
        families.Should().Contain(CF1);
        families.Should().Contain(CF2);
    }

    [Fact]
    public async Task CheckAndMutate_across_families()
    {
        var rk = new BigtableByteString("mf-cam");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF1, "trigger", "go", new BigtableVersion(1000)));

        var resp = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.Chain(
                RowFilters.FamilyNameRegex(CF1),
                RowFilters.ValueExact("go")),
            trueMutations: new[] { Mutations.SetCell(CF2, "result", "done", new BigtableVersion(2000)) });

        resp.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task MutateRows_different_families_in_batch()
    {
        var entries = new[]
        {
            Mutations.CreateEntry(new BigtableByteString("mf-batch1"),
                Mutations.SetCell(CF1, "a", "1", new BigtableVersion(1000))),
            Mutations.CreateEntry(new BigtableByteString("mf-batch2"),
                Mutations.SetCell(CF2, "b", "2", new BigtableVersion(1000))),
            Mutations.CreateEntry(new BigtableByteString("mf-batch3"),
                Mutations.SetCell(CF3, "c", "3", new BigtableVersion(1000))),
        };

        await Client.MutateRowsAsync(TN, entries);

        var r1 = await Client.ReadRowAsync(TN, new BigtableByteString("mf-batch1"));
        r1!.Families[0].Name.Should().Be(CF1);
        var r2 = await Client.ReadRowAsync(TN, new BigtableByteString("mf-batch2"));
        r2!.Families[0].Name.Should().Be(CF2);
        var r3 = await Client.ReadRowAsync(TN, new BigtableByteString("mf-batch3"));
        r3!.Families[0].Name.Should().Be(CF3);
    }

    [Fact]
    public async Task Row_with_data_in_only_one_family()
    {
        var rk = new BigtableByteString("mf-onefam");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF2, "col", "val", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.Should().HaveCount(1);
        row.Families[0].Name.Should().Be(CF2);
    }

    [Fact]
    public async Task All_families_deleted_means_row_gone()
    {
        var rk = new BigtableByteString("mf-allgone");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF1, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "b", "2", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromFamily(CF1),
            Mutations.DeleteFromFamily(CF2));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().BeNull();
    }

    [Fact]
    public async Task Interleave_across_families()
    {
        var rk = new BigtableByteString("mf-intlv");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF1, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF3, "c", "3", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("mf-intlv") } },
            Filter = RowFilters.Interleave(
                RowFilters.FamilyNameRegex(CF1),
                RowFilters.FamilyNameRegex(CF3))
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows[0].Families.Select(f => f.Name).Should().BeEquivalentTo(new[] { CF1, CF3 });
    }

    [Fact]
    public async Task Multiple_columns_per_family()
    {
        var rk = new BigtableByteString("mf-multicol");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF1, "x", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF1, "y", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "a", "3", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "b", "4", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "5", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        var cf1cols = row!.Families.First(f => f.Name == CF1).Columns.Count;
        var cf2cols = row.Families.First(f => f.Name == CF2).Columns.Count;
        cf1cols.Should().Be(2);
        cf2cols.Should().Be(3);
    }
}
