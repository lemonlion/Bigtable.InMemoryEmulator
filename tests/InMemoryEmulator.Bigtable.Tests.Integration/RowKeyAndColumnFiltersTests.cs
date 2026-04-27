using System.Collections.Generic;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for RowKeyRegex and ColumnQualifierExact/Regex filter behaviors.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
///   "row_key_regex_filter: Matches only cells from rows whose keys satisfy the given RE2 regex."
///   "column_qualifier_regex_filter: Matches only cells from columns whose qualifiers satisfy the given RE2 regex."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class RowKeyAndColumnFiltersTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "rk-col-filt";

    public RowKeyAndColumnFiltersTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task RowKeyRegex_prefix()
    {
        await Client.MutateRowAsync(TN, "rkc-foo-1",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "rkc-bar-1",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN,
            filter: RowFilters.RowKeyRegex("rkc-foo.*")))
            rows.Add(__row);
        rows.Should().ContainSingle();
        rows[0].Key.ToStringUtf8().Should().Be("rkc-foo-1");
    }

    [Fact]
    public async Task RowKeyRegex_suffix()
    {
        await Client.MutateRowAsync(TN, "rkc-alpha-end",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "rkc-beta-end",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "rkc-gamma-nope",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN,
            filter: RowFilters.RowKeyRegex(".*-end")))
            rows.Add(__row);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task RowKeyRegex_no_match()
    {
        await Client.MutateRowAsync(TN, "rkc-xyz",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN,
            filter: RowFilters.RowKeyRegex("rkc-zzz.*")))
            rows.Add(__row);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task RowKeyRegex_dot_star_matches_all()
    {
        await Client.MutateRowAsync(TN, "rkc-all-1",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "rkc-all-2",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN,
            filter: RowFilters.RowKeyRegex("rkc-all.*")))
            rows.Add(__row);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task RowKeyRegex_exact_match()
    {
        await Client.MutateRowAsync(TN, "rkc-exact",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "rkc-exact",
            RowFilters.RowKeyRegex("rkc-exact"));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task ColumnQualifierRegex_exact()
    {
        await Client.MutateRowAsync(TN, "rkc-cq1",
            Mutations.SetCell(CF, "target", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "other", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "rkc-cq1",
            RowFilters.ColumnQualifierRegex("target"));
        row!.Families[0].Columns.Should().ContainSingle();
    }

    [Fact]
    public async Task ColumnQualifierRegex_prefix()
    {
        await Client.MutateRowAsync(TN, "rkc-cq2",
            Mutations.SetCell(CF, "col_a", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col_b", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "other", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "rkc-cq2",
            RowFilters.ColumnQualifierRegex("col_.*"));
        row!.Families[0].Columns.Should().HaveCount(2);
    }

    [Fact]
    public async Task ColumnQualifierExact()
    {
        await Client.MutateRowAsync(TN, "rkc-cq3",
            Mutations.SetCell(CF, "find-me", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "not-me", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "rkc-cq3",
            RowFilters.ColumnQualifierExact("find-me"));
        row!.Families[0].Columns.Should().ContainSingle();
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("find-me");
    }

    [Fact]
    public async Task ColumnQualifierRegex_no_match()
    {
        await Client.MutateRowAsync(TN, "rkc-cq4",
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "rkc-cq4",
            RowFilters.ColumnQualifierRegex("nonexistent"));
        row.Should().BeNull();
    }

    [Fact]
    public async Task RowKeyRegex_with_column_filter_chain()
    {
        await Client.MutateRowAsync(TN, "rkc-chain-1",
            Mutations.SetCell(CF, "keep", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "drop", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "rkc-chain-2",
            Mutations.SetCell(CF, "keep", "v", new BigtableVersion(1000)));
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN,
            filter: RowFilters.Chain(
                RowFilters.RowKeyRegex("rkc-chain.*"),
                RowFilters.ColumnQualifierExact("keep"))))
            rows.Add(__row);
        rows.Should().HaveCount(2);
        foreach (var row in rows)
            row.Families[0].Columns.Should().ContainSingle();
    }

    [Fact]
    public async Task FamilyNameRegex_exact()
    {
        await Client.MutateRowAsync(TN, "rkc-fam1",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "rkc-fam1",
            RowFilters.FamilyNameRegex(CF));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task FamilyNameRegex_no_match()
    {
        await Client.MutateRowAsync(TN, "rkc-fam2",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "rkc-fam2",
            RowFilters.FamilyNameRegex("nonexistent"));
        row.Should().BeNull();
    }

    [Fact]
    public async Task RowKeyExact_match()
    {
        await Client.MutateRowAsync(TN, "rkc-rkex",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = new List<Row>();
        await foreach (var __row in Client.ReadRows(TN,
            filter: RowFilters.RowKeyExact("rkc-rkex")))
            rows.Add(__row);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task RowKeyExact_no_match()
    {
        await Client.MutateRowAsync(TN, "rkc-rkex2",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "rkc-rkex2",
            RowFilters.RowKeyExact("wrong-key"));
        row.Should().BeNull();
    }
}
