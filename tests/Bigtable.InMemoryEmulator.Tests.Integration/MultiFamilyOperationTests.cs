using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for multi-family read and write operations: cross-family reads, writes,
/// deletes, filters spanning families, and family ordering.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#family
///   "Family: A collection of user data indexed by row, column, and timestamp."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MultiFamilyOperationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "mfam-ops";

    public MultiFamilyOperationTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { "alpha", "beta", "gamma", "delta" });
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    #region Cross-family writes

    [Fact]
    public async Task Write_to_all_four_families()
    {
        await Client.MutateRowAsync(TN, "mf-all",
            Mutations.SetCell("alpha", "c", "a", new BigtableVersion(1000)),
            Mutations.SetCell("beta", "c", "b", new BigtableVersion(1000)),
            Mutations.SetCell("gamma", "c", "g", new BigtableVersion(1000)),
            Mutations.SetCell("delta", "c", "d", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mf-all");
        row!.Families.Should().HaveCount(4);
    }

    [Fact]
    public async Task Write_multiple_columns_per_family()
    {
        await Client.MutateRowAsync(TN, "mf-multi",
            Mutations.SetCell("alpha", "a1", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("alpha", "a2", "v2", new BigtableVersion(1000)),
            Mutations.SetCell("beta", "b1", "v3", new BigtableVersion(1000)),
            Mutations.SetCell("beta", "b2", "v4", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mf-multi");
        var alpha = row!.Families.First(f => f.Name == "alpha");
        alpha.Columns.Should().HaveCount(2);
        var beta = row.Families.First(f => f.Name == "beta");
        beta.Columns.Should().HaveCount(2);
    }

    #endregion

    #region Family ordering

    [Fact]
    public async Task Families_returned_in_sorted_order()
    {
        await Client.MutateRowAsync(TN, "mf-order",
            Mutations.SetCell("delta", "c", "d", new BigtableVersion(1000)),
            Mutations.SetCell("alpha", "c", "a", new BigtableVersion(1000)),
            Mutations.SetCell("gamma", "c", "g", new BigtableVersion(1000)),
            Mutations.SetCell("beta", "c", "b", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mf-order");
        var familyNames = row!.Families.Select(f => f.Name).ToList();
        familyNames.Should().BeInAscendingOrder();
    }

    #endregion

    #region Cross-family deletes

    [Fact]
    public async Task DeleteFromFamily_one_preserves_others()
    {
        await Client.MutateRowAsync(TN, "mf-del1",
            Mutations.SetCell("alpha", "c", "a", new BigtableVersion(1000)),
            Mutations.SetCell("beta", "c", "b", new BigtableVersion(1000)),
            Mutations.SetCell("gamma", "c", "g", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "mf-del1", Mutations.DeleteFromFamily("beta"));
        var row = await Client.ReadRowAsync(TN, "mf-del1");
        row!.Families.Select(f => f.Name).Should().BeEquivalentTo(new[] { "alpha", "gamma" });
    }

    [Fact]
    public async Task DeleteFromColumn_cross_family()
    {
        await Client.MutateRowAsync(TN, "mf-del2",
            Mutations.SetCell("alpha", "c1", "a1", new BigtableVersion(1000)),
            Mutations.SetCell("alpha", "c2", "a2", new BigtableVersion(1000)),
            Mutations.SetCell("beta", "c1", "b1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "mf-del2",
            Mutations.DeleteFromColumn("alpha", "c1"));
        var row = await Client.ReadRowAsync(TN, "mf-del2");
        var alphaCols = row!.Families.First(f => f.Name == "alpha").Columns;
        alphaCols.Should().ContainSingle().Which.Qualifier.ToStringUtf8().Should().Be("c2");
        row.Families.First(f => f.Name == "beta").Columns.Should().ContainSingle();
    }

    [Fact]
    public async Task Delete_all_families_removes_row()
    {
        await Client.MutateRowAsync(TN, "mf-delall",
            Mutations.SetCell("alpha", "c", "a", new BigtableVersion(1000)),
            Mutations.SetCell("beta", "c", "b", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "mf-delall",
            Mutations.DeleteFromFamily("alpha"),
            Mutations.DeleteFromFamily("beta"));
        var row = await Client.ReadRowAsync(TN, "mf-delall");
        row.Should().BeNull();
    }

    #endregion

    #region Cross-family reads with filters

    [Fact]
    public async Task Filter_to_single_family()
    {
        await Client.MutateRowAsync(TN, "mf-filt1",
            Mutations.SetCell("alpha", "c", "a", new BigtableVersion(1000)),
            Mutations.SetCell("beta", "c", "b", new BigtableVersion(1000)));
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("mf-filt1"),
            filter: RowFilters.FamilyNameRegex("alpha")))
        {
            row.Families.Should().ContainSingle().Which.Name.Should().Be("alpha");
        }
    }

    [Fact]
    public async Task Filter_to_two_families()
    {
        await Client.MutateRowAsync(TN, "mf-filt2",
            Mutations.SetCell("alpha", "c", "a", new BigtableVersion(1000)),
            Mutations.SetCell("beta", "c", "b", new BigtableVersion(1000)),
            Mutations.SetCell("gamma", "c", "g", new BigtableVersion(1000)));
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("mf-filt2"),
            filter: RowFilters.FamilyNameRegex("alpha|gamma")))
        {
            row.Families.Should().HaveCount(2);
            row.Families.Select(f => f.Name).Should().BeEquivalentTo(new[] { "alpha", "gamma" });
        }
    }

    [Fact]
    public async Task Chain_family_and_qualifier_filter()
    {
        await Client.MutateRowAsync(TN, "mf-filt3",
            Mutations.SetCell("alpha", "target", "hit", new BigtableVersion(1000)),
            Mutations.SetCell("alpha", "other", "miss", new BigtableVersion(1000)),
            Mutations.SetCell("beta", "target", "also-miss", new BigtableVersion(1000)));
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("mf-filt3"),
            filter: RowFilters.Chain(
                RowFilters.FamilyNameRegex("alpha"),
                RowFilters.ColumnQualifierExact("target"))))
        {
            row.Families.Should().ContainSingle();
            row.Families[0].Columns.Should().ContainSingle()
                .Which.Cells[0].Value.ToStringUtf8().Should().Be("hit");
        }
    }

    #endregion

    #region Cross-family CheckAndMutate

    [Fact]
    public async Task CaM_check_one_family_mutate_another()
    {
        await Client.MutateRowAsync(TN, "mf-cam",
            Mutations.SetCell("alpha", "flag", "ready", new BigtableVersion(1000)));
        var resp = await Client.CheckAndMutateRowAsync(TN, "mf-cam",
            RowFilters.Chain(
                RowFilters.FamilyNameRegex("alpha"),
                RowFilters.ColumnQualifierExact("flag"),
                RowFilters.ValueRegex("ready")),
            trueMutations: new[] { Mutations.SetCell("beta", "result", "done", new BigtableVersion(2000)) },
            falseMutations: null);
        resp.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, "mf-cam");
        row!.Families.First(f => f.Name == "beta").Columns[0].Cells[0]
            .Value.ToStringUtf8().Should().Be("done");
    }

    #endregion

    #region Cross-family ReadModifyWrite

    [Fact]
    public async Task RMW_across_families()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "mf-rmw",
            ReadModifyWriteRules.Append("alpha", "log", "entry1"),
            ReadModifyWriteRules.Increment("beta", "counter", 1));
        resp.Row.Families.Should().HaveCount(2);
    }

    #endregion

    #region Batch writes across families

    [Fact]
    public async Task Batch_entries_across_families()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mf-batch1",
                Mutations.SetCell("alpha", "c", "a", new BigtableVersion(1000)),
                Mutations.SetCell("gamma", "c", "g", new BigtableVersion(1000))),
            Mutations.CreateEntry("mf-batch2",
                Mutations.SetCell("beta", "c", "b", new BigtableVersion(1000)),
                Mutations.SetCell("delta", "c", "d", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);
        var r1 = await Client.ReadRowAsync(TN, "mf-batch1");
        r1!.Families.Should().HaveCount(2);
        var r2 = await Client.ReadRowAsync(TN, "mf-batch2");
        r2!.Families.Should().HaveCount(2);
    }

    #endregion

    #region Partial family data

    [Fact]
    public async Task Row_with_data_in_subset_of_families()
    {
        await Client.MutateRowAsync(TN, "mf-partial",
            Mutations.SetCell("alpha", "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mf-partial");
        row!.Families.Should().ContainSingle().Which.Name.Should().Be("alpha");
    }

    [Fact]
    public async Task Read_empty_family_returns_no_family_entry()
    {
        await Client.MutateRowAsync(TN, "mf-empty",
            Mutations.SetCell("alpha", "c", "v", new BigtableVersion(1000)));
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("mf-empty"),
            filter: RowFilters.FamilyNameRegex("beta")))
        {
            // Should not reach here since no data in beta
            true.Should().BeFalse("Should not find a row when filtering for empty family");
        }
    }

    #endregion
}
