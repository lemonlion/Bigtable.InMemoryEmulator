using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for column ordering, column qualifier edge cases, and binary qualifiers.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsresponse
///   Columns within a family are sorted by qualifier (bytewise).
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ColumnOrderingAndEdgeCaseTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "col-order";

    public ColumnOrderingAndEdgeCaseTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task Columns_sorted_lexicographically()
    {
        await Client.MutateRowAsync(TN, "co-r1",
            Mutations.SetCell(CF, "z", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "a", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "m", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "co-r1");
        var quals = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Empty_column_qualifier()
    {
        await Client.MutateRowAsync(TN, "co-r2",
            Mutations.SetCell(CF, "", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "co-r2");
        row!.Families[0].Columns[0].Qualifier.Length.Should().Be(0);
    }

    [Fact]
    public async Task Binary_column_qualifier()
    {
        var binQual = ByteString.CopyFrom(new byte[] { 0x00, 0x01, 0xFF });
        await Client.MutateRowAsync(TN, "co-r3",
            Mutations.SetCell(CF, binQual, ByteString.CopyFromUtf8("v"), new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "co-r3");
        row!.Families[0].Columns[0].Qualifier.ToByteArray()
            .Should().BeEquivalentTo(new byte[] { 0x00, 0x01, 0xFF });
    }

    [Fact]
    public async Task Many_columns_sorted()
    {
        var mutations = Enumerable.Range(0, 20)
            .Select(i => Mutations.SetCell(CF, $"col-{i:D2}", "v", new BigtableVersion(1000)))
            .ToArray();
        await Client.MutateRowAsync(TN, "co-r4", mutations);
        var row = await Client.ReadRowAsync(TN, "co-r4");
        var quals = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().BeInAscendingOrder();
        quals.Should().HaveCount(20);
    }

    [Fact]
    public async Task Column_with_special_characters()
    {
        await Client.MutateRowAsync(TN, "co-r5",
            Mutations.SetCell(CF, "col:with:colons", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col.with.dots", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "col-with-dashes", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "co-r5");
        row!.Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task Same_qualifier_multiple_versions()
    {
        await Client.MutateRowAsync(TN, "co-r6",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(2000)),
            Mutations.SetCell(CF, "c", "v3", new BigtableVersion(3000)));
        var row = await Client.ReadRowAsync(TN, "co-r6");
        row!.Families[0].Columns.Should().ContainSingle();
        row.Families[0].Columns[0].Cells.Should().HaveCount(3);
    }

    [Fact]
    public async Task Column_filter_exact_binary()
    {
        var binQual = ByteString.CopyFrom(new byte[] { 0x42 });
        await Client.MutateRowAsync(TN, "co-r7",
            Mutations.SetCell(CF, binQual, ByteString.CopyFromUtf8("v"), new BigtableVersion(1000)),
            Mutations.SetCell(CF, "text", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "co-r7",
            new RowFilter { ColumnQualifierRegexFilter = binQual });
        row!.Families[0].Columns.Should().ContainSingle();
    }

    [Fact]
    public async Task Unicode_column_qualifier()
    {
        await Client.MutateRowAsync(TN, "co-r8",
            Mutations.SetCell(CF, "café", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "co-r8");
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be("café");
    }

    [Fact]
    public async Task Long_column_qualifier()
    {
        var longQual = new string('x', 1000);
        await Client.MutateRowAsync(TN, "co-r9",
            Mutations.SetCell(CF, longQual, "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "co-r9");
        row!.Families[0].Columns[0].Qualifier.ToStringUtf8().Should().Be(longQual);
    }

    [Fact]
    public async Task Columns_from_different_writes_merged()
    {
        await Client.MutateRowAsync(TN, "co-r10",
            Mutations.SetCell(CF, "a", "1", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "co-r10",
            Mutations.SetCell(CF, "b", "2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "co-r10",
            Mutations.SetCell(CF, "c", "3", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "co-r10");
        row!.Families[0].Columns.Should().HaveCount(3);
        var quals = row.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Case_sensitive_column_names()
    {
        await Client.MutateRowAsync(TN, "co-r11",
            Mutations.SetCell(CF, "ABC", "upper", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "abc", "lower", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "co-r11");
        row!.Families[0].Columns.Should().HaveCount(2);
    }

    [Fact]
    public async Task Numeric_column_names_sorted_lexicographically()
    {
        // "9" > "10" lexicographically
        await Client.MutateRowAsync(TN, "co-r12",
            Mutations.SetCell(CF, "9", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "10", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "co-r12");
        var quals = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        quals[0].Should().Be("10"); // "10" < "9" lexicographically
        quals[1].Should().Be("9");
    }
}
