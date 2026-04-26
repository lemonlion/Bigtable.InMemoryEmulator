using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for MutateRow single-row operations with various mutation types and combinations.
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutaterowrequest
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutateRowSingleExtendedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "mrse-tests";
    private const string CF = "cf";
    private TableName TN => _fixture.GetTableName(Table);

    public MutateRowSingleExtendedTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF, "cf2" });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task SetCell_creates_new_row()
    {
        await Client.MutateRowAsync(TN, "mrse-new",
            Mutations.SetCell(CF, "c", "value", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mrse-new");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("value");
    }

    [Fact]
    public async Task SetCell_multiple_columns()
    {
        await Client.MutateRowAsync(TN, "mrse-multi",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mrse-multi");
        row!.Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task SetCell_multiple_families()
    {
        await Client.MutateRowAsync(TN, "mrse-fam",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "v2", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mrse-fam");
        row!.Families.Should().HaveCount(2);
    }

    [Fact]
    public async Task SetCell_empty_value()
    {
        await Client.MutateRowAsync(TN, "mrse-empty",
            Mutations.SetCell(CF, "c", "", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mrse-empty");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().BeEmpty();
    }

    [Fact]
    public async Task SetCell_unicode_value()
    {
        await Client.MutateRowAsync(TN, "mrse-unicode",
            Mutations.SetCell(CF, "c", "日本語テスト", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mrse-unicode");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("日本語テスト");
    }

    [Fact]
    public async Task SetCell_long_value()
    {
        var longVal = new string('x', 5000);
        await Client.MutateRowAsync(TN, "mrse-long",
            Mutations.SetCell(CF, "c", longVal, new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mrse-long");
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().HaveLength(5000);
    }

    [Fact]
    public async Task SetCell_binary_value()
    {
        var bytes = ByteString.CopyFrom(new byte[] { 0x00, 0x01, 0xFF, 0xFE });
        await Client.MutateRowAsync(TN, "mrse-bin",
            Mutations.SetCell(CF, ByteString.CopyFromUtf8("c"), bytes, new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mrse-bin");
        row!.Families[0].Columns[0].Cells[0].Value.ToByteArray().Should().BeEquivalentTo(bytes.ToByteArray());
    }

    [Fact]
    public async Task SetCell_additive_versions()
    {
        await Client.MutateRowAsync(TN, "mrse-add",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "mrse-add",
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, "mrse-add",
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));

        var row = await Client.ReadRowAsync(TN, "mrse-add");
        row!.Families[0].Columns[0].Cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task SetCell_same_timestamp_overwrites()
    {
        await Client.MutateRowAsync(TN, "mrse-over",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "mrse-over",
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "mrse-over");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task DeleteFromRow_makes_row_null()
    {
        await Client.MutateRowAsync(TN, "mrse-del",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "mrse-del", Mutations.DeleteFromRow());
        (await Client.ReadRowAsync(TN, "mrse-del")).Should().BeNull();
    }

    [Fact]
    public async Task DeleteFromFamily_partial()
    {
        await Client.MutateRowAsync(TN, "mrse-delfam",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("cf2", "c", "v2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "mrse-delfam", Mutations.DeleteFromFamily(CF));

        var row = await Client.ReadRowAsync(TN, "mrse-delfam");
        row!.Families.Should().ContainSingle().Which.Name.Should().Be("cf2");
    }

    [Fact]
    public async Task DeleteFromColumn_specific()
    {
        await Client.MutateRowAsync(TN, "mrse-delcol",
            Mutations.SetCell(CF, "keep", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "remove", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "mrse-delcol", Mutations.DeleteFromColumn(CF, "remove"));

        var row = await Client.ReadRowAsync(TN, "mrse-delcol");
        row!.Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("keep");
    }

    [Fact]
    public async Task Mixed_set_and_delete_in_one_call()
    {
        await Client.MutateRowAsync(TN, "mrse-mixop",
            Mutations.SetCell(CF, "old", "x", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, "mrse-mixop",
            Mutations.DeleteFromColumn(CF, "old"),
            Mutations.SetCell(CF, "new", "y", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, "mrse-mixop");
        row!.Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Nonexistent_family_throws()
    {
        var act = () => Client.MutateRowAsync(TN, "mrse-badfam",
            Mutations.SetCell("nonexistent", "c", "v", new BigtableVersion(1000)));
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Many_mutations_in_single_call()
    {
        var mutations = Enumerable.Range(0, 30)
            .Select(i => Mutations.SetCell(CF, $"col{i:D3}", $"val{i}", new BigtableVersion(1000)))
            .ToArray();
        await Client.MutateRowAsync(TN, "mrse-many", mutations);

        var row = await Client.ReadRowAsync(TN, "mrse-many");
        row!.Families[0].Columns.Should().HaveCount(30);
    }

    [Fact]
    public async Task Row_key_special_characters()
    {
        await Client.MutateRowAsync(TN, "mrse-special#key:with.dots/and:colons",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mrse-special#key:with.dots/and:colons");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Column_qualifier_special_characters()
    {
        await Client.MutateRowAsync(TN, "mrse-specialcol",
            Mutations.SetCell(CF, "a:b:c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "mrse-specialcol");
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("a:b:c");
    }

    [Fact]
    public async Task SetCell_then_delete_then_set()
    {
        await Client.MutateRowAsync(TN, "mrse-sds",
            Mutations.SetCell(CF, "c", "first", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "mrse-sds", Mutations.DeleteFromRow());
        await Client.MutateRowAsync(TN, "mrse-sds",
            Mutations.SetCell(CF, "c", "second", new BigtableVersion(2000)));

        var row = await Client.ReadRowAsync(TN, "mrse-sds");
        row!.Families[0].Columns[0].Cells.Should().ContainSingle()
            .Which.Value.ToStringUtf8().Should().Be("second");
    }
}
