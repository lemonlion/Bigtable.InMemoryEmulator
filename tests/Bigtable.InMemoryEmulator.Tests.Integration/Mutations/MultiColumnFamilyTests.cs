using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MultiColumnFamilyTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "mcf-tests";
    private const string CF1 = "family1";
    private const string CF2 = "family2";
    private const string CF3 = "family3";

    public MultiColumnFamilyTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF1, CF2, CF3 });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Write_to_different_families_in_single_mutation()
    {
        var rk = "mcf-single-mut";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF1, "a", "v1"),
            Mutations.SetCell(CF2, "b", "v2"),
            Mutations.SetCell(CF3, "c", "v3"));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.Should().HaveCount(3);
    }

    [Fact]
    public async Task Family_filter_returns_only_matching_family()
    {
        var rk = "mcf-fam-filter";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF1, "a", "v1"),
            Mutations.SetCell(CF2, "b", "v2"));

        var row = await Client.ReadRowAsync(TN, rk, RowFilters.FamilyNameExact(CF1));
        row.Should().NotBeNull();
        row!.Families.Should().ContainSingle().Which.Name.Should().Be(CF1);
    }

    [Fact]
    public async Task Family_regex_matches_multiple_families()
    {
        var rk = "mcf-fam-regex";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF1, "a", "v1"),
            Mutations.SetCell(CF2, "b", "v2"),
            Mutations.SetCell(CF3, "c", "v3"));

        var row = await Client.ReadRowAsync(TN, rk, RowFilters.FamilyNameRegex("family[12]"));
        row.Should().NotBeNull();
        row!.Families.Should().HaveCount(2);
        row.Families.Select(f => f.Name).Should().BeEquivalentTo(new[] { CF1, CF2 });
    }

    [Fact]
    public async Task Delete_from_one_family_preserves_others()
    {
        var rk = "mcf-del-one";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF1, "a", "v1"),
            Mutations.SetCell(CF2, "b", "v2"),
            Mutations.SetCell(CF3, "c", "v3"));

        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromFamily(CF2));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.Select(f => f.Name).Should().BeEquivalentTo(new[] { CF1, CF3 });
    }

    [Fact]
    public async Task Delete_from_all_families_removes_row()
    {
        var rk = "mcf-del-all";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF1, "a", "v1"),
            Mutations.SetCell(CF2, "b", "v2"));

        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromRow());

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().BeNull();
    }

    [Fact]
    public async Task Same_column_qualifier_in_different_families()
    {
        var rk = "mcf-same-col";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF1, "shared", "from-1"),
            Mutations.SetCell(CF2, "shared", "from-2"));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.Should().HaveCount(2);
        row.Families.Single(f => f.Name == CF1).Columns.Single().Cells.Single().Value.ToStringUtf8().Should().Be("from-1");
        row.Families.Single(f => f.Name == CF2).Columns.Single().Cells.Single().Value.ToStringUtf8().Should().Be("from-2");
    }

    [Fact]
    public async Task Column_qualifier_filter_across_families()
    {
        var rk = "mcf-col-filter";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF1, "target", "hit1"),
            Mutations.SetCell(CF1, "other", "miss"),
            Mutations.SetCell(CF2, "target", "hit2"),
            Mutations.SetCell(CF3, "nope", "miss2"));

        var row = await Client.ReadRowAsync(TN, rk, RowFilters.ColumnQualifierExact("target"));
        row.Should().NotBeNull();
        row!.Families.Should().HaveCount(2);
        var values = row.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Select(c => c.Value.ToStringUtf8()).ToList();
        values.Should().BeEquivalentTo(new[] { "hit1", "hit2" });
    }

    [Fact]
    public async Task ReadModifyWrite_append_to_different_families()
    {
        var rk = "mcf-rmw-append";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF1, "data", "hello"),
            Mutations.SetCell(CF2, "data", "world"));

        var result = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Append(CF1, "data", "-1"),
            ReadModifyWriteRules.Append(CF2, "data", "-2"));

        result.Row.Families.Single(f => f.Name == CF1).Columns.Single().Cells.Single().Value.ToStringUtf8().Should().Be("hello-1");
        result.Row.Families.Single(f => f.Name == CF2).Columns.Single().Cells.Single().Value.ToStringUtf8().Should().Be("world-2");
    }

    [Fact]
    public async Task CheckAndMutate_predicate_on_one_family_mutates_another()
    {
        var rk = "mcf-cam-cross";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF1, "flag", "yes"));

        var result = await Client.CheckAndMutateRowAsync(TN, rk,
            RowFilters.Chain(RowFilters.FamilyNameExact(CF1), RowFilters.ValueExact("yes")),
            trueMutations: new[] { Mutations.SetCell(CF2, "result", "done") });

        result.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.Single(f => f.Name == CF2).Columns.Single().Cells.Single().Value.ToStringUtf8().Should().Be("done");
    }

    [Fact]
    public async Task Families_returned_in_sorted_order()
    {
        var rk = "mcf-order";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF3, "c", "3"),
            Mutations.SetCell(CF1, "a", "1"),
            Mutations.SetCell(CF2, "b", "2"));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.Select(f => f.Name).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Delete_specific_column_from_one_family()
    {
        var rk = "mcf-del-col";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF1, "keep", "yes"),
            Mutations.SetCell(CF1, "drop", "no"),
            Mutations.SetCell(CF2, "keep2", "yes2"));

        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromColumn(CF1, "drop"));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.Single(f => f.Name == CF1).Columns.Select(c => c.Qualifier.ToStringUtf8())
            .Should().ContainSingle().Which.Should().Be("keep");
    }

    [Fact]
    public async Task Interleave_filters_from_different_families()
    {
        var rk = "mcf-inter-fam";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF1, "a", "v1"),
            Mutations.SetCell(CF2, "b", "v2"),
            Mutations.SetCell(CF3, "c", "v3"));

        var filter = RowFilters.Interleave(RowFilters.FamilyNameExact(CF1), RowFilters.FamilyNameExact(CF3));
        var row = await Client.ReadRowAsync(TN, rk, filter);

        row.Should().NotBeNull();
        row!.Families.Select(f => f.Name).Should().BeEquivalentTo(new[] { CF1, CF3 });
    }

    [Fact]
    public async Task MutateRows_batch_across_families()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("mcf-batch-1", Mutations.SetCell(CF1, "a", "v1"), Mutations.SetCell(CF2, "b", "v2")),
            Mutations.CreateEntry("mcf-batch-2", Mutations.SetCell(CF2, "c", "v3"), Mutations.SetCell(CF3, "d", "v4"))
        };
        await Client.MutateRowsAsync(TN, entries);

        (await Client.ReadRowAsync(TN, "mcf-batch-1"))!.Families.Should().HaveCount(2);
        (await Client.ReadRowAsync(TN, "mcf-batch-2"))!.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Family_name_is_case_sensitive()
    {
        var rk = "mcf-case";
        await Client.MutateRowAsync(TN, rk, Mutations.SetCell(CF1, "col", "val"));

        var row = await Client.ReadRowAsync(TN, rk, RowFilters.FamilyNameExact("Family1"));
        row.Should().BeNull();
    }

    [Fact]
    public async Task Delete_family_with_multiple_columns()
    {
        var rk = "mcf-del-fam-multi";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF1, "a", "1"),
            Mutations.SetCell(CF1, "b", "2"),
            Mutations.SetCell(CF1, "c", "3"),
            Mutations.SetCell(CF2, "x", "4"));

        await Client.MutateRowAsync(TN, rk, Mutations.DeleteFromFamily(CF1));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.Should().ContainSingle().Which.Name.Should().Be(CF2);
    }

    [Fact]
    public async Task ReadModifyWrite_increment_across_families()
    {
        var rk = "mcf-rmw-inc";
        var result = await Client.ReadModifyWriteRowAsync(TN, rk,
            ReadModifyWriteRules.Increment(CF1, "counter", 10),
            ReadModifyWriteRules.Increment(CF2, "counter", 20));

        result.Should().NotBeNull();
        result.Row.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Column_range_restricted_to_family()
    {
        var rk = "mcf-col-range";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF1, "a", "1"),
            Mutations.SetCell(CF1, "m", "2"),
            Mutations.SetCell(CF1, "z", "3"),
            Mutations.SetCell(CF2, "a", "4"),
            Mutations.SetCell(CF2, "m", "5"));

        var row = await Client.ReadRowAsync(TN, rk, RowFilters.ColumnRange(ColumnRange.Closed(CF1, "a", "m")));
        row.Should().NotBeNull();
        row!.Families.Should().ContainSingle().Which.Name.Should().Be(CF1);
        row.Families.Single().Columns.Select(c => c.Qualifier.ToStringUtf8()).Should().BeEquivalentTo(new[] { "a", "m" });
    }

    [Fact]
    public async Task Multiple_versions_in_multiple_families()
    {
        var rk = "mcf-multi-ver";
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF1, "col", "f1v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "col", "f2v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF1, "col", "f1v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF2, "col", "f2v2", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, rk, RowFilters.CellsPerColumnLimit(1));
        row.Should().NotBeNull();
        row!.Families.Single(f => f.Name == CF1).Columns.Single().Cells.Single().Value.ToStringUtf8().Should().Be("f1v2");
        row.Families.Single(f => f.Name == CF2).Columns.Single().Cells.Single().Value.ToStringUtf8().Should().Be("f2v2");
    }
}
