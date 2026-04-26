using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for multi-family read and write interactions — cross-family queries,
/// family-scoped mutations, filter combinations across families.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CrossFamilyInteractionTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "xfam-int";
    private const string CF1 = "cf1";
    private const string CF2 = "cf2";
    private const string CF3 = "cf3";
    private TableName TN => _fixture.GetTableName(Table);

    public CrossFamilyInteractionTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { CF1, CF2, CF3 });

        await Client.MutateRowAsync(TN, "xf-r1",
            Mutations.SetCell(CF1, "a", "cf1-a", new BigtableVersion(1000)),
            Mutations.SetCell(CF1, "b", "cf1-b", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "x", "cf2-x", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "y", "cf2-y", new BigtableVersion(1000)),
            Mutations.SetCell(CF3, "p", "cf3-p", new BigtableVersion(1000)));
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task ReadRow_returns_all_families()
    {
        var row = await Client.ReadRowAsync(TN, "xf-r1");
        row!.Families.Select(f => f.Name).Should().BeEquivalentTo(CF1, CF2, CF3);
    }

    [Fact]
    public async Task FamilyNameExact_filters_single_family()
    {
        var request = MakeRequest("xf-r1", RowFilters.FamilyNameExact(CF2));
        var families = await CollectFamilies(request);
        families.Should().ContainSingle(CF2);
    }

    [Fact]
    public async Task FamilyNameRegex_filters_multiple_families()
    {
        var request = MakeRequest("xf-r1", RowFilters.FamilyNameRegex("cf[12]"));
        var families = await CollectFamilies(request);
        families.Should().BeEquivalentTo(CF1, CF2);
    }

    [Fact]
    public async Task Interleave_two_family_exact_filters()
    {
        var request = MakeRequest("xf-r1",
            RowFilters.Interleave(
                RowFilters.FamilyNameExact(CF1),
                RowFilters.FamilyNameExact(CF3)));
        var families = await CollectFamilies(request);
        families.Should().BeEquivalentTo(CF1, CF3);
    }

    [Fact]
    public async Task Chain_family_and_column_across_families()
    {
        var request = MakeRequest("xf-r1",
            RowFilters.Chain(
                RowFilters.FamilyNameExact(CF1),
                RowFilters.ColumnQualifierExact("b")));
        var vals = await CollectValues(request);
        vals.Should().ContainSingle("cf1-b");
    }

    [Fact]
    public async Task Delete_one_family_preserves_others()
    {
        await Client.MutateRowAsync(TN, "xf-del1",
            Mutations.SetCell(CF1, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "b", "v2", new BigtableVersion(1000)),
            Mutations.SetCell(CF3, "c", "v3", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "xf-del1", Mutations.DeleteFromFamily(CF2));

        var row = await Client.ReadRowAsync(TN, "xf-del1");
        row!.Families.Select(f => f.Name).Should().BeEquivalentTo(CF1, CF3);
    }

    [Fact]
    public async Task Delete_all_families_removes_row()
    {
        await Client.MutateRowAsync(TN, "xf-del-all",
            Mutations.SetCell(CF1, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "b", "v2", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "xf-del-all",
            Mutations.DeleteFromFamily(CF1),
            Mutations.DeleteFromFamily(CF2));

        var row = await Client.ReadRowAsync(TN, "xf-del-all");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Write_to_all_three_families_then_read_filtered()
    {
        await Client.MutateRowAsync(TN, "xf-3fam",
            Mutations.SetCell(CF1, "c", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF3, "c", "3", new BigtableVersion(1000)));

        var request = MakeRequest("xf-3fam", RowFilters.FamilyNameExact(CF3));
        var vals = await CollectValues(request);
        vals.Should().ContainSingle("3");
    }

    [Fact]
    public async Task Batch_mutation_across_families()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("xf-batch",
                Mutations.SetCell(CF1, "a", "v1", new BigtableVersion(1000)),
                Mutations.SetCell(CF2, "b", "v2", new BigtableVersion(1000)),
                Mutations.SetCell(CF3, "c", "v3", new BigtableVersion(1000)))
        };
        await Client.MutateRowsAsync(TN, entries);

        var row = await Client.ReadRowAsync(TN, "xf-batch");
        row!.Families.Should().HaveCount(3);
    }

    [Fact]
    public async Task CheckAndMutate_predicate_on_one_family_mutates_another()
    {
        await Client.MutateRowAsync(TN, "xf-cam",
            Mutations.SetCell(CF1, "trigger", "go", new BigtableVersion(1000)));

        var result = await Client.CheckAndMutateRowAsync(TN, "xf-cam",
            RowFilters.Chain(RowFilters.FamilyNameExact(CF1), RowFilters.ValueExact("go")),
            trueMutations: new[] { Mutations.SetCell(CF2, "result", "done", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, "xf-cam");
        var cf2 = row!.Families.First(f => f.Name == CF2);
        cf2.Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("done");
    }

    [Fact]
    public async Task ReadModifyWrite_across_families()
    {
        var response = await Client.ReadModifyWriteRowAsync(TN, "xf-rmw",
            ReadModifyWriteRules.Append(CF1, "a", "cf1"),
            ReadModifyWriteRules.Append(CF2, "b", "cf2"),
            ReadModifyWriteRules.Append(CF3, "c", "cf3"));

        response.Row.Families.Should().HaveCount(3);
    }

    [Fact]
    public async Task ColumnRange_scoped_to_family()
    {
        await Client.MutateRowAsync(TN, "xf-colr",
            Mutations.SetCell(CF1, "aa", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF1, "bb", "v2", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "aa", "v3", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "bb", "v4", new BigtableVersion(1000)));

        // ColumnRange is scoped to CF1
        var request = MakeRequest("xf-colr",
            RowFilters.ColumnRange(ColumnRange.Closed(CF1, "aa", "aa")));
        var vals = await CollectValues(request);
        vals.Should().ContainSingle("v1");
    }

    [Fact]
    public async Task CellsPerRowLimit_across_families()
    {
        await Client.MutateRowAsync(TN, "xf-cprl",
            Mutations.SetCell(CF1, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF1, "b", "v2", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "v3", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "d", "v4", new BigtableVersion(1000)),
            Mutations.SetCell(CF3, "e", "v5", new BigtableVersion(1000)));

        var request = MakeRequest("xf-cprl", RowFilters.CellsPerRowLimit(3));
        var cellCount = 0;
        await foreach (var row in Client.ReadRows(request))
            cellCount += row.Families.Sum(f => f.Columns.Sum(c => c.Cells.Count));

        cellCount.Should().Be(3);
    }

    [Fact]
    public async Task Condition_filter_across_families()
    {
        await Client.MutateRowAsync(TN, "xf-cond",
            Mutations.SetCell(CF1, "flag", "active", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "data", "important", new BigtableVersion(1000)),
            Mutations.SetCell(CF3, "meta", "info", new BigtableVersion(1000)));

        var request = MakeRequest("xf-cond",
            RowFilters.Condition(
                RowFilters.Chain(RowFilters.FamilyNameExact(CF1), RowFilters.ValueExact("active")),
                RowFilters.FamilyNameExact(CF2),
                RowFilters.FamilyNameExact(CF3)));
        var families = await CollectFamilies(request);
        families.Should().ContainSingle(CF2);
    }

    [Fact]
    public async Task StripValue_preserves_family_structure()
    {
        var request = MakeRequest("xf-r1", RowFilters.StripValueTransformer());
        await foreach (var row in Client.ReadRows(request))
        {
            row.Families.Should().HaveCount(3);
            foreach (var fam in row.Families)
                foreach (var col in fam.Columns)
                    col.Cells[0].Value.Length.Should().Be(0);
        }
    }

    [Fact]
    public async Task Families_returned_in_sorted_order()
    {
        var row = await Client.ReadRowAsync(TN, "xf-r1");
        var names = row!.Families.Select(f => f.Name).ToList();
        names.Should().ContainInOrder(CF1, CF2, CF3);
    }

    [Fact]
    public async Task Delete_column_from_specific_family()
    {
        await Client.MutateRowAsync(TN, "xf-delcol",
            Mutations.SetCell(CF1, "shared", "cf1-val", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "shared", "cf2-val", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "xf-delcol",
            Mutations.DeleteFromColumn(CF1, "shared"));

        var row = await Client.ReadRowAsync(TN, "xf-delcol");
        row!.Families.Should().HaveCount(1);
        row.Families[0].Name.Should().Be(CF2);
    }

    [Fact]
    public async Task ValueRange_applies_across_all_families()
    {
        await Client.MutateRowAsync(TN, "xf-vr",
            Mutations.SetCell(CF1, "c", "apple", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "banana", new BigtableVersion(1000)),
            Mutations.SetCell(CF3, "c", "cherry", new BigtableVersion(1000)));

        var request = MakeRequest("xf-vr",
            RowFilters.ValueRange(ValueRange.Closed("banana", "cherry")));
        var vals = await CollectValues(request);
        vals.Should().HaveCount(2);
        vals.Should().Contain("banana");
        vals.Should().Contain("cherry");
    }

    private ReadRowsRequest MakeRequest(string key, RowFilter filter) =>
        new()
        {
            TableNameAsTableName = TN,
            Filter = filter,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8(key) } }
        };

    private async Task<List<string>> CollectFamilies(ReadRowsRequest request)
    {
        var families = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
                families.Add(f.Name);
        return families;
    }

    private async Task<List<string>> CollectValues(ReadRowsRequest request)
    {
        var vals = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
            foreach (var cell in c.Cells)
                vals.Add(cell.Value.ToStringUtf8());
        return vals;
    }
}
