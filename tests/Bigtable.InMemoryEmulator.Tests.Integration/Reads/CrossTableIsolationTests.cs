using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class CrossTableIsolationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table1 = "iso-t1";
    private const string Table2 = "iso-t2";
    private const string CF = "cf";

    public CrossTableIsolationTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table1, new[] { CF });
        await _fixture.CreateTableAsync(Table2, new[] { CF });
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private TableName TN1 => _fixture.GetTableName(Table1);
    private TableName TN2 => _fixture.GetTableName(Table2);

    [Fact]
    public async Task Writes_to_one_table_not_visible_in_other()
    {
        await Client.MutateRowAsync(TN1, "r1", Mutations.SetCell(CF, "c", "v1"));
        var row = await Client.ReadRowAsync(TN2, "r1");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Same_row_key_different_tables()
    {
        await Client.MutateRowAsync(TN1, "shared", Mutations.SetCell(CF, "c", "from-t1"));
        await Client.MutateRowAsync(TN2, "shared", Mutations.SetCell(CF, "c", "from-t2"));
        var r1 = await Client.ReadRowAsync(TN1, "shared");
        var r2 = await Client.ReadRowAsync(TN2, "shared");
        r1!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("from-t1");
        r2!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("from-t2");
    }

    [Fact]
    public async Task Delete_in_one_table_no_effect_on_other()
    {
        await Client.MutateRowAsync(TN1, "del", Mutations.SetCell(CF, "c", "v1"));
        await Client.MutateRowAsync(TN2, "del", Mutations.SetCell(CF, "c", "v2"));
        await Client.MutateRowAsync(TN1, "del", Mutations.DeleteFromRow());
        var r2 = await Client.ReadRowAsync(TN2, "del");
        r2.Should().NotBeNull();
    }

    [Fact]
    public async Task ReadRows_scoped_to_table()
    {
        await Client.MutateRowAsync(TN1, "a", Mutations.SetCell(CF, "c", "v1"));
        await Client.MutateRowAsync(TN1, "b", Mutations.SetCell(CF, "c", "v2"));
        await Client.MutateRowAsync(TN2, "c", Mutations.SetCell(CF, "c", "v3"));
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN1)) rows.Add(r);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task CheckAndMutate_scoped_to_table()
    {
        await Client.MutateRowAsync(TN1, "cam", Mutations.SetCell(CF, "c", "yes"));
        // CheckAndMutate on table2 should not find the row from table1
        var resp = await Client.CheckAndMutateRowAsync(TN2, "cam",
            RowFilters.PassAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(CF, "c", "created") });
        resp.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN2, "cam");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task ReadModifyWrite_scoped_to_table()
    {
        await Client.MutateRowAsync(TN1, "rmw", Mutations.SetCell(CF, "c", "hello"));
        var resp = await Client.ReadModifyWriteRowAsync(TN2, "rmw",
            ReadModifyWriteRules.Append(CF, "c", "world"));
        resp.Row.Families[0].Columns[0].Cells.First().Value.ToStringUtf8().Should().Be("world");
        // Table1 should be unaffected
        var r1 = await Client.ReadRowAsync(TN1, "rmw");
        r1!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("hello");
    }

    [Fact]
    public async Task MutateRows_batch_scoped_to_table()
    {
        var entries = new[]
        {
            Mutations.CreateEntry("x", Mutations.SetCell(CF, "c", "vx")),
            Mutations.CreateEntry("y", Mutations.SetCell(CF, "c", "vy"))
        };
        await Client.MutateRowsAsync(TN1, entries);
        var rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN2)) rows.Add(r);
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Count_rows_per_table()
    {
        for (int i = 0; i < 5; i++)
            await Client.MutateRowAsync(TN1, $"r{i}", Mutations.SetCell(CF, "c", "v"));
        for (int i = 0; i < 3; i++)
            await Client.MutateRowAsync(TN2, $"r{i}", Mutations.SetCell(CF, "c", "v"));
        var t1Rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN1)) t1Rows.Add(r);
        var t2Rows = new List<Row>();
        await foreach (var r in Client.ReadRows(TN2)) t2Rows.Add(r);
        t1Rows.Should().HaveCount(5);
        t2Rows.Should().HaveCount(3);
    }
}
