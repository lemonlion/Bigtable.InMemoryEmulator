using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for timestamp validation: -1 (server-assigned), ms-alignment, boundaries.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
///   "timestamp_micros: must be >= -1, if -1 server will assign. Must be multiple of 1000."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class TimestampValidationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public TimestampValidationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync("ts-valid", new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("ts-valid");

    #region Server-assigned timestamp (-1)

    [Fact]
    public async Task Server_assigned_timestamp_minus_one()
    {
        // Ref: timestamp_micros=-1 means server assigns the timestamp
        await Client.MutateRowAsync(TN, "ts-server-1",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(-1)));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("ts-server-1")))
            rows.Add(row);
        rows.Should().ContainSingle();
        var cell = rows[0].Families[0].Columns[0].Cells[0];
        // Server-assigned timestamp should be > 0
        cell.TimestampMicros.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Server_assigned_timestamp_is_ms_aligned()
    {
        await Client.MutateRowAsync(TN, "ts-server-align",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(-1)));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("ts-server-align")))
            rows.Add(row);
        var ts = rows[0].Families[0].Columns[0].Cells[0].TimestampMicros;
        (ts % 1000).Should().Be(0, "server-assigned timestamp should be millisecond-aligned");
    }

    [Fact]
    public async Task Server_assigned_timestamps_are_monotonic()
    {
        await Client.MutateRowAsync(TN, "ts-mono-1",
            Mutations.SetCell(CF, "c", "v1", new BigtableVersion(-1)));
        await Client.MutateRowAsync(TN, "ts-mono-2",
            Mutations.SetCell(CF, "c", "v2", new BigtableVersion(-1)));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN,
            RowSet.FromRowKeys("ts-mono-1", "ts-mono-2")))
            rows.Add(row);

        var ts1 = rows[0].Families[0].Columns[0].Cells[0].TimestampMicros;
        var ts2 = rows[1].Families[0].Columns[0].Cells[0].TimestampMicros;
        ts2.Should().BeGreaterThanOrEqualTo(ts1);
    }

    #endregion

    #region Valid explicit timestamps

    [Fact]
    public async Task Timestamp_zero_is_valid()
    {
        await Client.MutateRowAsync(TN, "ts-zero",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(0)));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("ts-zero")))
            rows.Add(row);
        rows.Should().ContainSingle();
        rows[0].Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(0);
    }

    [Fact]
    public async Task Timestamp_1000_microseconds_is_valid()
    {
        await Client.MutateRowAsync(TN, "ts-1000",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("ts-1000")))
            rows.Add(row);
        rows[0].Families[0].Columns[0].Cells[0].TimestampMicros.Should().Be(1_000_000);
    }

    [Fact]
    public async Task Large_timestamp_is_valid()
    {
        // A timestamp far in the future
        var ts = new BigtableVersion(999_999_999_000);
        await Client.MutateRowAsync(TN, "ts-large",
            Mutations.SetCell(CF, "c", "v", ts));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("ts-large")))
            rows.Add(row);
        rows.Should().ContainSingle();
    }

    #endregion

    #region Not ms-aligned (should fail)

    [Fact]
    [Trait(TestTraits.Target, TestTraits.InMemoryOnly)]
    public async Task Timestamp_not_ms_aligned_throws()
    {
        // Ref: timestamp must be multiple of 1000 microseconds
        // BigtableVersion constructor multiplies by 1000, so we use raw protobuf
        var mutation = new Mutation
        {
            SetCell = new Mutation.Types.SetCell
            {
                FamilyName = CF,
                ColumnQualifier = ByteString.CopyFromUtf8("c"),
                Value = ByteString.CopyFromUtf8("v"),
                TimestampMicros = 1001 // NOT a multiple of 1000
            }
        };
        var request = new MutateRowRequest
        {
            TableNameAsTableName = TN,
            RowKey = ByteString.CopyFromUtf8("ts-unaligned"),
        };
        request.Mutations.Add(mutation);
        var act = () => _fixture.ServiceApiClient.MutateRowAsync(request);
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    [Fact]
    [Trait(TestTraits.Target, TestTraits.InMemoryOnly)]
    public async Task Timestamp_999_not_aligned_throws()
    {
        var mutation = new Mutation
        {
            SetCell = new Mutation.Types.SetCell
            {
                FamilyName = CF,
                ColumnQualifier = ByteString.CopyFromUtf8("c"),
                Value = ByteString.CopyFromUtf8("v"),
                TimestampMicros = 999
            }
        };
        var request = new MutateRowRequest
        {
            TableNameAsTableName = TN,
            RowKey = ByteString.CopyFromUtf8("ts-999"),
        };
        request.Mutations.Add(mutation);
        var act = () => _fixture.ServiceApiClient.MutateRowAsync(request);
        await act.Should().ThrowAsync<Grpc.Core.RpcException>();
    }

    #endregion

    #region Timestamp ordering in reads

    [Fact]
    public async Task Versions_read_in_descending_timestamp_order()
    {
        for (int i = 1; i <= 5; i++)
            await Client.MutateRowAsync(TN, "ts-order",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(i * 1000)));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("ts-order")))
            rows.Add(row);
        var cells = rows[0].Families[0].Columns[0].Cells;
        cells.Should().HaveCount(5);
        // Cells should be newest first
        for (int i = 1; i < cells.Count; i++)
            cells[i - 1].TimestampMicros.Should().BeGreaterThan(cells[i].TimestampMicros);
    }

    [Fact]
    public async Task Same_timestamp_overwrites_value()
    {
        await Client.MutateRowAsync(TN, "ts-overwrite",
            Mutations.SetCell(CF, "c", "old", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "ts-overwrite",
            Mutations.SetCell(CF, "c", "new", new BigtableVersion(1000)));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("ts-overwrite")))
            rows.Add(row);
        var cells = rows[0].Families[0].Columns[0].Cells;
        cells.Should().ContainSingle();
        cells[0].Value.ToStringUtf8().Should().Be("new");
    }

    [Fact]
    public async Task Older_timestamp_inserted_after_newer_still_sorted()
    {
        // Write newer first, then older
        await Client.MutateRowAsync(TN, "ts-outoforder",
            Mutations.SetCell(CF, "c", "newer", new BigtableVersion(5000)));
        await Client.MutateRowAsync(TN, "ts-outoforder",
            Mutations.SetCell(CF, "c", "older", new BigtableVersion(1000)));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("ts-outoforder")))
            rows.Add(row);
        var cells = rows[0].Families[0].Columns[0].Cells;
        cells.Should().HaveCount(2);
        cells[0].Value.ToStringUtf8().Should().Be("newer");
        cells[1].Value.ToStringUtf8().Should().Be("older");
    }

    #endregion

    #region Timestamp in batch operations

    [Fact]
    public async Task Batch_with_server_assigned_timestamps()
    {
        var entries = Enumerable.Range(0, 5).Select(i =>
            Mutations.CreateEntry($"ts-batch-{i:D2}",
                Mutations.SetCell(CF, "c", $"v{i}", new BigtableVersion(-1)))).ToArray();
        await Client.MutateRowsAsync(TN, entries);

        for (int i = 0; i < 5; i++)
        {
            var rows = new List<Row>();
            await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys($"ts-batch-{i:D2}")))
                rows.Add(row);
            rows.Should().ContainSingle();
            rows[0].Families[0].Columns[0].Cells[0].TimestampMicros.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task Mixed_server_and_explicit_timestamps()
    {
        await Client.MutateRowAsync(TN, "ts-mixed",
            Mutations.SetCell(CF, "auto", "v1", new BigtableVersion(-1)),
            Mutations.SetCell(CF, "explicit", "v2", new BigtableVersion(5000)));

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("ts-mixed")))
            rows.Add(row);
        rows.Should().ContainSingle();

        var autoCol = rows[0].Families[0].Columns
            .First(c => c.Qualifier.ToStringUtf8() == "auto");
        autoCol.Cells[0].TimestampMicros.Should().BeGreaterThan(0);

        var explicitCol = rows[0].Families[0].Columns
            .First(c => c.Qualifier.ToStringUtf8() == "explicit");
        explicitCol.Cells[0].TimestampMicros.Should().Be(5_000_000);
    }

    #endregion
}
