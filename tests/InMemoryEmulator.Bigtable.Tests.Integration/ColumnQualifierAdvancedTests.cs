using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for column qualifier operations — binary qualifiers, special characters,
/// ordering, and range queries.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ColumnQualifierAdvancedTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "col-qual-adv";
    private const string CF = "cf";

    public ColumnQualifierAdvancedTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task Single_character_qualifier()
    {
        var rk = new BigtableByteString("cqa-1char");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "x", "val", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("x");
    }

    [Fact]
    public async Task Empty_qualifier()
    {
        var rk = new BigtableByteString("cqa-empty");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "", "val", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().BeEmpty();
    }

    [Fact]
    public async Task Qualifier_with_special_chars()
    {
        var rk = new BigtableByteString("cqa-special");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "col.with.dots", "val", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("col.with.dots");
    }

    [Fact]
    public async Task Qualifier_with_hash()
    {
        var rk = new BigtableByteString("cqa-hash");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "prefix#suffix", "val", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("prefix#suffix");
    }

    [Fact]
    public async Task Binary_qualifier()
    {
        var rk = new BigtableByteString("cqa-bin");
        var qual = new byte[] { 0x00, 0xFF, 0x01 };
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, ByteString.CopyFrom(qual),
                ByteString.CopyFromUtf8("val"), new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Qualifier.ToByteArray().Should().BeEquivalentTo(qual);
    }

    [Fact]
    public async Task ColumnQualifierExact_filter()
    {
        var rk = new BigtableByteString("cqa-exact");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "target", "hit", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "other", "miss", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("cqa-exact") } },
            Filter = RowFilters.ColumnQualifierExact("target")
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows[0].Families[0].Columns.Should().HaveCount(1);
        rows[0].Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("target");
    }

    [Fact]
    public async Task ColumnQualifierRegex_filter()
    {
        var rk = new BigtableByteString("cqa-regex");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "data_name", "1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "data_age", "2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "meta_id", "3", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("cqa-regex") } },
            Filter = RowFilters.ColumnQualifierRegex("data_.*")
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().HaveCount(2);
        cols.Should().Contain("data_name");
        cols.Should().Contain("data_age");
    }

    [Fact]
    public async Task ColumnRange_closed_open()
    {
        var rk = new BigtableByteString("cqa-range");
        foreach (var c in new[] { "a", "b", "c", "d", "e" })
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, c, c, new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("cqa-range") } },
            Filter = RowFilters.ColumnRange(ColumnRange.ClosedOpen(CF, "b", "e"))
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().BeEquivalentTo(new[] { "b", "c", "d" });
    }

    [Fact]
    public async Task ColumnRange_closed_closed()
    {
        var rk = new BigtableByteString("cqa-cc");
        foreach (var c in new[] { "a", "b", "c", "d" })
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, c, c, new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("cqa-cc") } },
            Filter = RowFilters.ColumnRange(ColumnRange.Closed(CF, "b", "c"))
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().BeEquivalentTo(new[] { "b", "c" });
    }

    [Fact]
    public async Task ColumnRange_open_open()
    {
        var rk = new BigtableByteString("cqa-oo");
        foreach (var c in new[] { "a", "b", "c", "d", "e" })
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, c, c, new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("cqa-oo") } },
            Filter = RowFilters.ColumnRange(ColumnRange.Open(CF, "a", "e"))
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        var cols = rows[0].Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().BeEquivalentTo(new[] { "b", "c", "d" });
    }

    [Fact]
    public async Task Columns_sorted_lexicographically()
    {
        var rk = new BigtableByteString("cqa-sort");
        foreach (var c in new[] { "z", "a", "m" })
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, c, c, new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        var cols = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        cols.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Many_columns_in_single_row()
    {
        var rk = new BigtableByteString("cqa-many");
        for (int i = 0; i < 50; i++)
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell(CF, $"col{i:D3}", $"v{i}", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns.Should().HaveCount(50);
    }

    [Fact]
    public async Task Qualifier_with_unicode_characters()
    {
        var rk = new BigtableByteString("cqa-unicode");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "名前", "val", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("名前");
    }

    [Fact]
    public async Task Multiple_qualifiers_with_same_prefix()
    {
        var rk = new BigtableByteString("cqa-prefix");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "user:name", "john", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "user:age", "30", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell(CF, "user:email", "j@x.com", new BigtableVersion(1000)));

        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("cqa-prefix") } },
            Filter = RowFilters.ColumnQualifierRegex("user:.*")
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(request))
            rows.Add(row);

        rows[0].Families[0].Columns.Should().HaveCount(3);
    }
}
