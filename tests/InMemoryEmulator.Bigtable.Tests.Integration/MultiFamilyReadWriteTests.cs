using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for multi-family read and write operations.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsresponse
///   "Rows are returned in order. Each row has families sorted by name."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MultiFamilyReadWriteTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF1 = "alpha";
    private const string CF2 = "beta";
    private const string CF3 = "gamma";
    private const string Table = "mf-rw";

    public MultiFamilyReadWriteTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF1, CF2, CF3 });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task Write_and_read_three_families()
    {
        await Client.MutateRowAsync(TN, "mfrw-r1",
            Mutations.SetCell(CF1, "c", "a", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "b", new BigtableVersion(1000)),
            Mutations.SetCell(CF3, "c", "g", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mfrw-r1");
        row!.Families.Should().HaveCount(3);
    }

    [Fact]
    public async Task Families_sorted_by_name()
    {
        await Client.MutateRowAsync(TN, "mfrw-r2",
            Mutations.SetCell(CF3, "c", "g", new BigtableVersion(1000)),
            Mutations.SetCell(CF1, "c", "a", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "b", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mfrw-r2");
        var names = row!.Families.Select(f => f.Name).ToList();
        names.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Filter_single_family()
    {
        await Client.MutateRowAsync(TN, "mfrw-r3",
            Mutations.SetCell(CF1, "c", "a", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "b", new BigtableVersion(1000)),
            Mutations.SetCell(CF3, "c", "g", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mfrw-r3",
            RowFilters.FamilyNameRegex(CF2));
        row!.Families.Should().ContainSingle();
        row.Families[0].Name.Should().Be(CF2);
    }

    [Fact]
    public async Task Filter_two_families_via_regex()
    {
        await Client.MutateRowAsync(TN, "mfrw-r4",
            Mutations.SetCell(CF1, "c", "a", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "b", new BigtableVersion(1000)),
            Mutations.SetCell(CF3, "c", "g", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mfrw-r4",
            RowFilters.FamilyNameRegex("alpha|beta"));
        row!.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Delete_one_family_preserves_others()
    {
        await Client.MutateRowAsync(TN, "mfrw-r5",
            Mutations.SetCell(CF1, "c", "a", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "b", new BigtableVersion(1000)),
            Mutations.SetCell(CF3, "c", "g", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "mfrw-r5", Mutations.DeleteFromFamily(CF2));
        var row = await Client.ReadRowAsync(TN, "mfrw-r5");
        row!.Families.Should().HaveCount(2);
        row.Families.Select(f => f.Name).Should().NotContain(CF2);
    }

    [Fact]
    public async Task ReadModifyWrite_across_families()
    {
        var resp = await Client.ReadModifyWriteRowAsync(TN, "mfrw-r6",
            ReadModifyWriteRules.Append(CF1, "c", "hello"),
            ReadModifyWriteRules.Append(CF2, "c", "world"));
        resp.Row.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task CheckAndMutate_across_families()
    {
        await Client.MutateRowAsync(TN, "mfrw-r7",
            Mutations.SetCell(CF1, "flag", "yes", new BigtableVersion(1000)));
        await Client.CheckAndMutateRowAsync(TN, "mfrw-r7",
            RowFilters.Chain(RowFilters.FamilyNameRegex(CF1), RowFilters.ValueRegex("yes")),
            new[] { Mutations.SetCell(CF2, "result", "confirmed", new BigtableVersion(2000)) },
            null);
        var row = await Client.ReadRowAsync(TN, "mfrw-r7");
        row!.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Columns_sorted_within_family()
    {
        await Client.MutateRowAsync(TN, "mfrw-r8",
            Mutations.SetCell(CF1, "z", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF1, "a", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF1, "m", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mfrw-r8");
        var qualifiers = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        qualifiers.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Multiple_columns_per_family()
    {
        await Client.MutateRowAsync(TN, "mfrw-r9",
            Mutations.SetCell(CF1, "c1", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF1, "c2", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF1, "c3", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c1", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mfrw-r9");
        row!.Families.First(f => f.Name == CF1).Columns.Should().HaveCount(3);
        row.Families.First(f => f.Name == CF2).Columns.Should().ContainSingle();
    }

    [Fact]
    public async Task Interleave_across_families()
    {
        await Client.MutateRowAsync(TN, "mfrw-r10",
            Mutations.SetCell(CF1, "c", "a", new BigtableVersion(1000)),
            Mutations.SetCell(CF2, "c", "b", new BigtableVersion(1000)),
            Mutations.SetCell(CF3, "c", "g", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mfrw-r10",
            RowFilters.Interleave(
                RowFilters.FamilyNameRegex(CF1),
                RowFilters.FamilyNameRegex(CF3)));
        row!.Families.Should().HaveCount(2);
        row.Families.Select(f => f.Name).Should().Contain(CF1).And.Contain(CF3);
    }
}
