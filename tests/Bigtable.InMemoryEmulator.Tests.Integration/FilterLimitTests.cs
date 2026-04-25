using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;
using Grpc.Core;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for filter nesting depth and size limits.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#rowfilter
///   Filter nesting depth limit: 20
///   Filter serialized size limit: 20480 bytes
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class FilterLimitTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public FilterLimitTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync("filter-limit", new[] { CF });
        await _fixture.Client.MutateRowAsync(_fixture.GetTableName("filter-limit"), "r1",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
    }
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("filter-limit");

    private RowFilter BuildNestedChain(int depth)
    {
        var inner = RowFilters.PassAllFilter();
        for (int i = 0; i < depth; i++)
            inner = RowFilters.Chain(inner, RowFilters.PassAllFilter());
        return inner;
    }

    [Fact]
    public async Task Moderate_nesting_depth_succeeds()
    {
        // Depth 5 should work fine
        var filter = BuildNestedChain(5);
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter))
            rows.Add(row);
        rows.Should().ContainSingle();
    }

    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task Deep_nesting_exceeding_limit_throws()
    {
        // Build filter with depth > 20
        var filter = BuildNestedChain(25);
        var act = async () =>
        {
            await foreach (var _ in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter)) { }
        };
        await act.Should().ThrowAsync<RpcException>();
    }

    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task Deeply_nested_interleave_exceeds_limit()
    {
        var inner = RowFilters.PassAllFilter();
        for (int i = 0; i < 25; i++)
            inner = RowFilters.Interleave(inner, RowFilters.PassAllFilter());

        var act = async () =>
        {
            await foreach (var _ in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), inner)) { }
        };
        await act.Should().ThrowAsync<RpcException>();
    }

    [Trait(TestTraits.Target, TestTraits.GcpOnly)]
    [Fact]
    public async Task Deeply_nested_condition_exceeds_limit()
    {
        var inner = RowFilters.PassAllFilter();
        for (int i = 0; i < 25; i++)
            inner = RowFilters.Condition(RowFilters.PassAllFilter(), inner, RowFilters.PassAllFilter());

        var act = async () =>
        {
            await foreach (var _ in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), inner)) { }
        };
        await act.Should().ThrowAsync<RpcException>();
    }

    [Fact]
    public async Task Complex_but_within_limit_filter()
    {
        // Build something complex but under 20 depth
        var filter = RowFilters.Chain(
            RowFilters.Interleave(
                RowFilters.Chain(
                    RowFilters.FamilyNameExact(CF),
                    RowFilters.CellsPerColumnLimit(1)),
                RowFilters.Chain(
                    RowFilters.PassAllFilter(),
                    RowFilters.CellsPerColumnLimit(2))),
            RowFilters.CellsPerColumnLimit(1));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter))
            rows.Add(row);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Wide_interleave_within_limits()
    {
        // Many parallel branches but shallow depth
        var branches = Enumerable.Range(0, 10)
            .Select(_ => RowFilters.PassAllFilter())
            .ToArray();
        var filter = RowFilters.Interleave(branches);

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("r1"), filter))
            rows.Add(row);
        rows.Should().ContainSingle();
    }
}
