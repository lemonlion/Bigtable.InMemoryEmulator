using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for regex-based value filters and exact value match.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
///   "value_regex_filter: Matches only cells with values that satisfy the given RE2 regex."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ValueRegexFilterTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";
    private const string Table = "val-regex";

    public ValueRegexFilterTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task ValueRegex_exact_match()
    {
        await Client.MutateRowAsync(TN, "vr-r1",
            Mutations.SetCell(CF, "c", "hello", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "vr-r1",
            RowFilters.ValueRegex("hello"));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task ValueRegex_no_match()
    {
        await Client.MutateRowAsync(TN, "vr-r2",
            Mutations.SetCell(CF, "c", "hello", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "vr-r2",
            RowFilters.ValueRegex("goodbye"));
        row.Should().BeNull();
    }

    [Fact]
    public async Task ValueRegex_pattern()
    {
        await Client.MutateRowAsync(TN, "vr-r3",
            Mutations.SetCell(CF, "c", "abc123", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "vr-r3",
            RowFilters.ValueRegex("[a-z]+[0-9]+"));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task ValueRegex_filters_specific_versions()
    {
        await Client.MutateRowAsync(TN, "vr-r4",
            Mutations.SetCell(CF, "c", "yes", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "no", new BigtableVersion(2000)));
        var row = await Client.ReadRowAsync(TN, "vr-r4",
            RowFilters.ValueRegex("yes"));
        row!.Families[0].Columns[0].Cells.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("yes");
    }

    [Fact]
    public async Task ValueRegex_across_columns()
    {
        await Client.MutateRowAsync(TN, "vr-r5",
            Mutations.SetCell(CF, "a", "match-me", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "skip", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "match-me", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "vr-r5",
            RowFilters.ValueRegex("match-me"));
        row!.Families[0].Columns.Should().HaveCount(2);
    }

    [Fact]
    public async Task ValueRegex_dot_star()
    {
        await Client.MutateRowAsync(TN, "vr-r6",
            Mutations.SetCell(CF, "c", "anything", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "vr-r6",
            RowFilters.ValueRegex(".*"));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task ValueRegex_binary()
    {
        var binVal = ByteString.CopyFrom(new byte[] { 0x01, 0x02, 0x03 });
        await Client.MutateRowAsync(TN, "vr-r7",
            Mutations.SetCell(CF, "c", binVal, new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "vr-r7",
            new RowFilter { ValueRegexFilter = ByteString.CopyFrom(new byte[] { 0x01, 0x02, 0x03 }) });
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task ValueExact_match()
    {
        await Client.MutateRowAsync(TN, "vr-r8",
            Mutations.SetCell(CF, "c", "exact", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "vr-r8",
            RowFilters.ValueExact("exact"));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task ValueExact_no_match()
    {
        await Client.MutateRowAsync(TN, "vr-r9",
            Mutations.SetCell(CF, "c", "value", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "vr-r9",
            RowFilters.ValueExact("other"));
        row.Should().BeNull();
    }

    [Fact]
    public async Task ValueRegex_case_sensitive()
    {
        await Client.MutateRowAsync(TN, "vr-r10",
            Mutations.SetCell(CF, "c", "Hello", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "vr-r10",
            RowFilters.ValueRegex("hello"));
        row.Should().BeNull();
    }

    [Fact]
    public async Task ValueRegex_empty_matches_empty()
    {
        await Client.MutateRowAsync(TN, "vr-r11",
            Mutations.SetCell(CF, "c", "", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "vr-r11",
            RowFilters.ValueRegex(""));
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task ValueRegex_with_chain_strip()
    {
        await Client.MutateRowAsync(TN, "vr-r12",
            Mutations.SetCell(CF, "a", "keep", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "drop", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "vr-r12",
            RowFilters.Chain(
                RowFilters.ValueRegex("keep"),
                RowFilters.StripValueTransformer()));
        row!.Families[0].Columns.Should().ContainSingle();
        row.Families[0].Columns[0].Cells[0].Value.Length.Should().Be(0);
    }
}
