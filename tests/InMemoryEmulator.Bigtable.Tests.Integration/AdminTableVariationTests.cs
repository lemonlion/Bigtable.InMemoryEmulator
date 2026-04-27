using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for admin table operations: create, modify column families, GC rules.
/// Ref: https://cloud.google.com/bigtable/docs/reference/admin/rpc/google.bigtable.admin.v2
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class AdminTableVariationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;

    public AdminTableVariationTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() { }
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Create_table_with_single_family()
    {
        await _fixture.CreateTableAsync("adm-sf", new[] { "cf" });
        await Client.MutateRowAsync(_fixture.GetTableName("adm-sf"), "row1",
            Mutations.SetCell("cf", "c", "val", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(_fixture.GetTableName("adm-sf"), "row1");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Create_table_with_multiple_families()
    {
        await _fixture.CreateTableAsync("adm-mf", new[] { "cf1", "cf2", "cf3" });
        await Client.MutateRowAsync(_fixture.GetTableName("adm-mf"), "row1",
            Mutations.SetCell("cf1", "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "v2", new BigtableVersion(1000)),
            Mutations.SetCell("cf3", "c", "v3", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(_fixture.GetTableName("adm-mf"), "row1");
        row!.Families.Should().HaveCount(3);
    }

    [Fact]
    public async Task Mutate_to_nonexistent_family_fails()
    {
        await _fixture.CreateTableAsync("adm-nf", new[] { "cf" });
        var act = () => Client.MutateRowAsync(_fixture.GetTableName("adm-nf"), "row1",
            Mutations.SetCell("nonexistent", "c", "v", new BigtableVersion(1000)));
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task ReadRow_from_empty_table()
    {
        await _fixture.CreateTableAsync("adm-empty", new[] { "cf" });
        var row = await Client.ReadRowAsync(_fixture.GetTableName("adm-empty"), "nonexistent");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Write_read_delete_write_read()
    {
        await _fixture.CreateTableAsync("adm-wrdwr", new[] { "cf" });
        var tn = _fixture.GetTableName("adm-wrdwr");

        await Client.MutateRowAsync(tn, "row1", Mutations.SetCell("cf", "c", "first", new BigtableVersion(1000)));
        var r1 = await Client.ReadRowAsync(tn, "row1");
        r1!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("first");

        await Client.MutateRowAsync(tn, "row1", Mutations.DeleteFromRow());
        var r2 = await Client.ReadRowAsync(tn, "row1");
        r2.Should().BeNull();

        await Client.MutateRowAsync(tn, "row1", Mutations.SetCell("cf", "c", "second", new BigtableVersion(2000)));
        var r3 = await Client.ReadRowAsync(tn, "row1");
        r3!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task Two_tables_are_independent()
    {
        await _fixture.CreateTableAsync("adm-ind1", new[] { "cf" });
        await _fixture.CreateTableAsync("adm-ind2", new[] { "cf" });

        await Client.MutateRowAsync(_fixture.GetTableName("adm-ind1"), "row1",
            Mutations.SetCell("cf", "c", "table1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(_fixture.GetTableName("adm-ind2"), "row1",
            Mutations.SetCell("cf", "c", "table2", new BigtableVersion(1000)));

        var r1 = await Client.ReadRowAsync(_fixture.GetTableName("adm-ind1"), "row1");
        var r2 = await Client.ReadRowAsync(_fixture.GetTableName("adm-ind2"), "row1");

        r1!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("table1");
        r2!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("table2");
    }

    [Fact]
    public async Task Table_allows_rows_with_same_key_different_families()
    {
        await _fixture.CreateTableAsync("adm-samek", new[] { "cf1", "cf2" });
        var tn = _fixture.GetTableName("adm-samek");

        await Client.MutateRowAsync(tn, "shared-key",
            Mutations.SetCell("cf1", "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "v2", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(tn, "shared-key");
        row!.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task Table_allows_many_columns_per_family()
    {
        await _fixture.CreateTableAsync("adm-manycol", new[] { "cf" });
        var tn = _fixture.GetTableName("adm-manycol");

        var mutations = Enumerable.Range(0, 50)
            .Select(i => Mutations.SetCell("cf", $"col_{i:D3}", $"val_{i}", new BigtableVersion(1000)))
            .ToArray();
        await Client.MutateRowAsync(tn, "wide-row", mutations);

        var row = await Client.ReadRowAsync(tn, "wide-row");
        row!.Families[0].Columns.Should().HaveCount(50);
    }

    [Fact]
    public async Task Table_allows_many_rows()
    {
        await _fixture.CreateTableAsync("adm-manyrow", new[] { "cf" });
        var tn = _fixture.GetTableName("adm-manyrow");

        for (int i = 0; i < 200; i++)
            await Client.MutateRowAsync(tn, $"r-{i:D4}",
                Mutations.SetCell("cf", "c", $"v{i}", new BigtableVersion(1000)));

        var count = 0;
        await foreach (var row in Client.ReadRows(new ReadRowsRequest { TableNameAsTableName = tn }))
            count++;
        count.Should().Be(200);
    }

    [Fact]
    public async Task Delete_from_family_clears_only_that_family()
    {
        await _fixture.CreateTableAsync("adm-delfam", new[] { "cf1", "cf2" });
        var tn = _fixture.GetTableName("adm-delfam");

        await Client.MutateRowAsync(tn, "row1",
            Mutations.SetCell("cf1", "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "v2", new BigtableVersion(1000)));

        await Client.MutateRowAsync(tn, "row1", Mutations.DeleteFromFamily("cf1"));

        var row = await Client.ReadRowAsync(tn, "row1");
        row!.Families.Should().ContainSingle();
        row.Families[0].Name.Should().Be("cf2");
    }

    [Fact]
    public async Task Delete_all_data_from_row()
    {
        await _fixture.CreateTableAsync("adm-delrow", new[] { "cf" });
        var tn = _fixture.GetTableName("adm-delrow");

        await Client.MutateRowAsync(tn, "row1",
            Mutations.SetCell("cf", "c1", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf", "c2", "v2", new BigtableVersion(1000)));

        await Client.MutateRowAsync(tn, "row1", Mutations.DeleteFromRow());
        var row = await Client.ReadRowAsync(tn, "row1");
        row.Should().BeNull();
    }

    [Fact]
    public async Task Overwrite_value_preserves_older_version()
    {
        await _fixture.CreateTableAsync("adm-over", new[] { "cf" });
        var tn = _fixture.GetTableName("adm-over");

        await Client.MutateRowAsync(tn, "row1",
            Mutations.SetCell("cf", "c", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(tn, "row1",
            Mutations.SetCell("cf", "c", "new", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(tn, "row1");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task Same_timestamp_overwrites_value()
    {
        await _fixture.CreateTableAsync("adm-samets", new[] { "cf" });
        var tn = _fixture.GetTableName("adm-samets");

        await Client.MutateRowAsync(tn, "row1",
            Mutations.SetCell("cf", "c", "first", new BigtableVersion(1000)));
        await Client.MutateRowAsync(tn, "row1",
            Mutations.SetCell("cf", "c", "second", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(tn, "row1");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(1);
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("second");
    }

    [Fact]
    public async Task Multiple_mutations_in_single_call()
    {
        await _fixture.CreateTableAsync("adm-multi", new[] { "cf" });
        var tn = _fixture.GetTableName("adm-multi");

        await Client.MutateRowAsync(tn, "row1",
            Mutations.SetCell("cf", "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell("cf", "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell("cf", "c", "3", new BigtableVersion(1000)),
            Mutations.SetCell("cf", "d", "4", new BigtableVersion(1000)),
            Mutations.SetCell("cf", "e", "5", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(tn, "row1");
        row!.Families[0].Columns.Should().HaveCount(5);
    }

    [Fact]
    public async Task Delete_from_column_clears_only_that_column()
    {
        await _fixture.CreateTableAsync("adm-delcol", new[] { "cf" });
        var tn = _fixture.GetTableName("adm-delcol");

        await Client.MutateRowAsync(tn, "row1",
            Mutations.SetCell("cf", "keep", "v", new BigtableVersion(1000)),
            Mutations.SetCell("cf", "remove", "v", new BigtableVersion(1000)));

        await Client.MutateRowAsync(tn, "row1", Mutations.DeleteFromColumn("cf", "remove"));

        var row = await Client.ReadRowAsync(tn, "row1");
        row!.Families[0].Columns.Should().ContainSingle();
        row.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("keep");
    }

    [Fact]
    public async Task ReadRows_returns_rows_in_lex_order()
    {
        await _fixture.CreateTableAsync("adm-order", new[] { "cf" });
        var tn = _fixture.GetTableName("adm-order");

        await Client.MutateRowAsync(tn, "z-last", Mutations.SetCell("cf", "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(tn, "a-first", Mutations.SetCell("cf", "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(tn, "m-middle", Mutations.SetCell("cf", "c", "v", new BigtableVersion(1000)));

        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(new ReadRowsRequest { TableNameAsTableName = tn }))
            keys.Add(row.Key.ToStringUtf8());

        keys.Should().BeInAscendingOrder();
    }
}
