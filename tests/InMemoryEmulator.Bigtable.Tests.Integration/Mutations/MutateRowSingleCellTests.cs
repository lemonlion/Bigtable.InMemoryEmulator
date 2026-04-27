using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MutateRowSingleCellTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);
    private const string Table = "mr-sc";
    private const string CF = "cf";

    public MutateRowSingleCellTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF });

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task Write_string_value()
    {
        await Client.MutateRowAsync(TN, "r1", Mutations.SetCell(CF, "c", "hello"));
        var row = await Client.ReadRowAsync(TN, "r1");
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("hello");
    }

    [Fact]
    public async Task Write_empty_string()
    {
        await Client.MutateRowAsync(TN, "r2", Mutations.SetCell(CF, "c", ""));
        var row = await Client.ReadRowAsync(TN, "r2");
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("");
    }

    [Fact]
    public async Task Write_binary_value()
    {
        var data = new byte[] { 0x00, 0x01, 0xFF };
        await Client.MutateRowAsync(TN, "r3", Mutations.SetCell(CF, "c", ByteString.CopyFrom(data), new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "r3");
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToByteArray().Should().BeEquivalentTo(data);
    }

    [Fact]
    public async Task Write_with_explicit_timestamp()
    {
        await Client.MutateRowAsync(TN, "r4", Mutations.SetCell(CF, "c", "v", new BigtableVersion(5000)));
        var row = await Client.ReadRowAsync(TN, "r4");
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().TimestampMicros.Should().Be(5000000);
    }

    [Fact]
    public async Task Write_assigns_server_timestamp()
    {
        await Client.MutateRowAsync(TN, "r5", Mutations.SetCell(CF, "c", "v"));
        var row = await Client.ReadRowAsync(TN, "r5");
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().TimestampMicros.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Write_multiple_columns()
    {
        await Client.MutateRowAsync(TN, "r6",
            Mutations.SetCell(CF, "a", "1"),
            Mutations.SetCell(CF, "b", "2"),
            Mutations.SetCell(CF, "c", "3"));
        var row = await Client.ReadRowAsync(TN, "r6");
        row!.Families.SelectMany(f => f.Columns).Should().HaveCount(3);
    }

    [Fact]
    public async Task Write_preserves_row_key()
    {
        await Client.MutateRowAsync(TN, "my-key", Mutations.SetCell(CF, "c", "v"));
        var row = await Client.ReadRowAsync(TN, "my-key");
        row!.Key.ToStringUtf8().Should().Be("my-key");
    }

    [Fact]
    public async Task Write_preserves_family_name()
    {
        await Client.MutateRowAsync(TN, "r7", Mutations.SetCell(CF, "c", "v"));
        var row = await Client.ReadRowAsync(TN, "r7");
        row!.Families.Single().Name.Should().Be(CF);
    }

    [Fact]
    public async Task Write_preserves_column_qualifier()
    {
        await Client.MutateRowAsync(TN, "r8", Mutations.SetCell(CF, "mycolumn", "v"));
        var row = await Client.ReadRowAsync(TN, "r8");
        row!.Families.SelectMany(f => f.Columns).Single().Qualifier.ToStringUtf8().Should().Be("mycolumn");
    }

    [Fact]
    public async Task Write_long_value()
    {
        var val = new string('a', 1000);
        await Client.MutateRowAsync(TN, "r9", Mutations.SetCell(CF, "c", val));
        var row = await Client.ReadRowAsync(TN, "r9");
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().HaveLength(1000);
    }

    [Fact]
    public async Task Write_long_column_qualifier()
    {
        var qual = new string('q', 100);
        await Client.MutateRowAsync(TN, "r10", Mutations.SetCell(CF, qual, "v"));
        var row = await Client.ReadRowAsync(TN, "r10");
        row!.Families.SelectMany(f => f.Columns).Single().Qualifier.ToStringUtf8().Should().HaveLength(100);
    }

    [Fact]
    public async Task Write_to_nonexistent_row_creates_it()
    {
        var row1 = await Client.ReadRowAsync(TN, "new-row");
        row1.Should().BeNull();
        await Client.MutateRowAsync(TN, "new-row", Mutations.SetCell(CF, "c", "v"));
        var row2 = await Client.ReadRowAsync(TN, "new-row");
        row2.Should().NotBeNull();
    }

    [Fact]
    public async Task Write_numeric_as_string()
    {
        await Client.MutateRowAsync(TN, "r11", Mutations.SetCell(CF, "c", "42"));
        var row = await Client.ReadRowAsync(TN, "r11");
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("42");
    }

    [Fact]
    public async Task Write_unicode_value()
    {
        await Client.MutateRowAsync(TN, "r12", Mutations.SetCell(CF, "c", "héllo wörld"));
        var row = await Client.ReadRowAsync(TN, "r12");
        row!.Families.SelectMany(f => f.Columns).SelectMany(c => c.Cells)
            .Single().Value.ToStringUtf8().Should().Be("héllo wörld");
    }

    [Fact]
    public async Task Write_returns_without_error()
    {
        // MutateRowAsync should complete without throwing
        var act = () => Client.MutateRowAsync(TN, "r13", Mutations.SetCell(CF, "c", "v"));
        await act.Should().NotThrowAsync();
    }
}
