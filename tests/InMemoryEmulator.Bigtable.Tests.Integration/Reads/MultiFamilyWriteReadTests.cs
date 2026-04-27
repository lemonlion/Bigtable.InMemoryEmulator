using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MultiFamilyWriteReadTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "mf-wr";
    private const string CF1 = "fam1";
    private const string CF2 = "fam2";
    private const string CF3 = "fam3";

    public MultiFamilyWriteReadTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF1, CF2, CF3 });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Write_to_multiple_families()
    {
        await Client.MutateRowAsync(TN, "r1",
            Mutations.SetCell(CF1, "c", "v1"),
            Mutations.SetCell(CF2, "c", "v2"),
            Mutations.SetCell(CF3, "c", "v3"));
        var row = await Client.ReadRowAsync(TN, "r1");
        row!.Families.Should().HaveCount(3);
    }

    [Fact]
    public async Task Family_names_sorted()
    {
        await Client.MutateRowAsync(TN, "r2",
            Mutations.SetCell(CF3, "c", "v3"),
            Mutations.SetCell(CF1, "c", "v1"),
            Mutations.SetCell(CF2, "c", "v2"));
        var row = await Client.ReadRowAsync(TN, "r2");
        row!.Families.Select(f => f.Name).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Delete_one_family_preserves_others()
    {
        await Client.MutateRowAsync(TN, "r3",
            Mutations.SetCell(CF1, "c", "v1"),
            Mutations.SetCell(CF2, "c", "v2"),
            Mutations.SetCell(CF3, "c", "v3"));
        await Client.MutateRowAsync(TN, "r3", Mutations.DeleteFromFamily(CF2));
        var row = await Client.ReadRowAsync(TN, "r3");
        row!.Families.Should().HaveCount(2);
        row.Families.Select(f => f.Name).Should().NotContain(CF2);
    }

    [Fact]
    public async Task Filter_single_family_from_multi()
    {
        await Client.MutateRowAsync(TN, "r4",
            Mutations.SetCell(CF1, "a", "v1"),
            Mutations.SetCell(CF2, "b", "v2"));
        var row = await Client.ReadRowAsync(TN, "r4", RowFilters.FamilyNameExact(CF2));
        row!.Families.Should().ContainSingle().Which.Name.Should().Be(CF2);
    }

    [Fact]
    public async Task Multiple_columns_per_family()
    {
        await Client.MutateRowAsync(TN, "r5",
            Mutations.SetCell(CF1, "a", "1"),
            Mutations.SetCell(CF1, "b", "2"),
            Mutations.SetCell(CF1, "c", "3"),
            Mutations.SetCell(CF2, "x", "4"),
            Mutations.SetCell(CF2, "y", "5"));
        var row = await Client.ReadRowAsync(TN, "r5");
        var f1 = row!.Families.First(f => f.Name == CF1);
        var f2 = row.Families.First(f => f.Name == CF2);
        f1.Columns.Should().HaveCount(3);
        f2.Columns.Should().HaveCount(2);
    }

    [Fact]
    public async Task Columns_sorted_within_family()
    {
        await Client.MutateRowAsync(TN, "r6",
            Mutations.SetCell(CF1, "z", "1"),
            Mutations.SetCell(CF1, "a", "2"),
            Mutations.SetCell(CF1, "m", "3"));
        var row = await Client.ReadRowAsync(TN, "r6");
        row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Delete_column_from_specific_family()
    {
        await Client.MutateRowAsync(TN, "r7",
            Mutations.SetCell(CF1, "c", "v1"),
            Mutations.SetCell(CF2, "c", "v2")); // same column name, different families
        await Client.MutateRowAsync(TN, "r7", Mutations.DeleteFromColumn(CF1, "c"));
        var row = await Client.ReadRowAsync(TN, "r7");
        row!.Families.Should().ContainSingle().Which.Name.Should().Be(CF2);
    }

    [Fact]
    public async Task ReadModifyWrite_on_specific_family()
    {
        await Client.MutateRowAsync(TN, "r8",
            Mutations.SetCell(CF1, "c", "hello"),
            Mutations.SetCell(CF2, "c", "world"));
        var resp = await Client.ReadModifyWriteRowAsync(TN, "r8",
            ReadModifyWriteRules.Append(CF1, "c", "!"));
        // CF1 should be modified, CF2 untouched
        var row = await Client.ReadRowAsync(TN, "r8");
        var f2 = row!.Families.First(f => f.Name == CF2);
        f2.Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("world");
    }

    [Fact]
    public async Task CheckAndMutate_with_family_predicate()
    {
        await Client.MutateRowAsync(TN, "r9", Mutations.SetCell(CF1, "c", "val"));
        var resp = await Client.CheckAndMutateRowAsync(TN, "r9",
            RowFilters.FamilyNameExact(CF2),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF2, "c", "created") });
        resp.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, "r9");
        row!.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Interleave_families_filter()
    {
        await Client.MutateRowAsync(TN, "r10",
            Mutations.SetCell(CF1, "c", "v1"),
            Mutations.SetCell(CF2, "c", "v2"),
            Mutations.SetCell(CF3, "c", "v3"));
        var filter = RowFilters.Interleave(
            RowFilters.FamilyNameExact(CF1),
            RowFilters.FamilyNameExact(CF3));
        var row = await Client.ReadRowAsync(TN, "r10", filter);
        row!.Families.Should().HaveCount(2);
        row.Families.Select(f => f.Name).Should().Contain(CF1).And.Contain(CF3);
    }

    [Fact]
    public async Task Batch_write_to_multiple_families()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("br1",
                Mutations.SetCell(CF1, "c", "v1"),
                Mutations.SetCell(CF2, "c", "v2")),
            Mutations.CreateEntry("br2",
                Mutations.SetCell(CF2, "c", "v3"),
                Mutations.SetCell(CF3, "c", "v4"))
        };
        await Client.MutateRowsAsync(TN, entries);
        var r1 = await Client.ReadRowAsync(TN, "br1");
        var r2 = await Client.ReadRowAsync(TN, "br2");
        r1!.Families.Should().HaveCount(2);
        r2!.Families.Should().HaveCount(2);
    }
}
