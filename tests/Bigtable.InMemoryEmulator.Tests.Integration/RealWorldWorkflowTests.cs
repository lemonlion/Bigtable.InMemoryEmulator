using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// End-to-end workflow scenarios simulating real-world patterns —
/// user profiles, time-series, event sourcing, wide-column.
/// Ref: https://cloud.google.com/bigtable/docs/schema-design
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class RealWorldWorkflowTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private BigtableClient Client => _fixture.Client;
    private const string Table = "rw-workflow";
    private const string CF = "profile";
    private const string Metrics = "metrics";
    private const string Events = "events";
    private TableName TN => _fixture.GetTableName(Table);

    public RealWorldWorkflowTests(EmulatorSession session) =>
        _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync() =>
        await _fixture.CreateTableAsync(Table, new[] { CF, Metrics, Events });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task User_profile_crud()
    {
        // Create user
        await Client.MutateRowAsync(TN, "user#alice",
            Mutations.SetCell(CF, "name", "Alice Smith", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "email", "alice@example.com", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "status", "active", new BigtableVersion(1000)));

        // Read user
        var row = await Client.ReadRowAsync(TN, "user#alice");
        row.Should().NotBeNull();

        // Update email
        await Client.MutateRowAsync(TN, "user#alice",
            Mutations.SetCell(CF, "email", "alice.new@example.com", new BigtableVersion(2000)));

        // Verify update
        row = await Client.ReadRowAsync(TN, "user#alice");
        var email = row!.Families.First(f => f.Name == CF).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "email")
            .Cells.OrderByDescending(c => c.TimestampMicros).First()
            .Value.ToStringUtf8();
        email.Should().Be("alice.new@example.com");

        // Delete user
        await Client.MutateRowAsync(TN, "user#alice", Mutations.DeleteFromRow());
        (await Client.ReadRowAsync(TN, "user#alice")).Should().BeNull();
    }

    [Fact]
    public async Task Time_series_write_and_scan()
    {
        // Write hourly metrics
        for (int hour = 0; hour < 24; hour++)
        {
            var ts = new DateTime(2024, 6, 15, hour, 0, 0, DateTimeKind.Utc);
            await Client.MutateRowAsync(TN, $"ts#device1#{ts:yyyyMMddHH}",
                Mutations.SetCell(Metrics, "temp", $"{20 + hour}", new BigtableVersion(ts)),
                Mutations.SetCell(Metrics, "humidity", $"{60 + hour % 10}", new BigtableVersion(ts)));
        }

        // Scan a range of hours
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFromUtf8("ts#device1#2024061508"),
                        EndKeyClosed = ByteString.CopyFromUtf8("ts#device1#2024061512")
                    }
                }
            }
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());

        keys.Should().HaveCount(5); // hours 08, 09, 10, 11, 12
    }

    [Fact]
    public async Task Event_sourcing_append_pattern()
    {
        // Add events as separate columns with incrementing names
        for (int i = 0; i < 5; i++)
        {
            var ts = new BigtableVersion((i + 1) * 1000);
            await Client.MutateRowAsync(TN, "order#12345",
                Mutations.SetCell(Events, $"event{i:D3}", $"{{\"type\":\"event{i}\"}}", ts));
        }

        var row = await Client.ReadRowAsync(TN, "order#12345");
        var eventFamily = row!.Families.First(f => f.Name == Events);
        eventFamily.Columns.Should().HaveCount(5);
    }

    [Fact]
    public async Task Counter_pattern_with_increment()
    {
        // Increment page view counter
        for (int i = 0; i < 10; i++)
            await Client.ReadModifyWriteRowAsync(TN, "page#/home",
                ReadModifyWriteRules.Increment(Metrics, "views", 1));

        var row = await Client.ReadRowAsync(TN, "page#/home");
        var bytes = row!.Families.First(f => f.Name == Metrics).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "views")
            .Cells[0].Value.ToByteArray();
        var val = BitConverter.ToInt64(bytes.Reverse().ToArray(), 0);
        val.Should().Be(10);
    }

    [Fact]
    public async Task Scan_by_prefix_pattern()
    {
        // Write data for multiple users
        for (int i = 0; i < 5; i++)
        {
            await Client.MutateRowAsync(TN, $"user#u{i:D3}",
                Mutations.SetCell(CF, "name", $"User {i}", new BigtableVersion(1000)));
            await Client.MutateRowAsync(TN, $"admin#a{i:D3}",
                Mutations.SetCell(CF, "name", $"Admin {i}", new BigtableVersion(1000)));
        }

        // Scan only users
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.RowKeyRegex("user#.*")
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            if (row.Key.ToStringUtf8().StartsWith("user#u"))
                keys.Add(row.Key.ToStringUtf8());

        keys.Should().HaveCount(5);
    }

    [Fact]
    public async Task Conditional_update_pattern()
    {
        // Set initial status
        await Client.MutateRowAsync(TN, "order#99",
            Mutations.SetCell(CF, "status", "pending", new BigtableVersion(1000)));

        // Only update if status is "pending"
        var result = await Client.CheckAndMutateRowAsync(TN, "order#99",
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.ValueExact("pending")),
            trueMutations: new[] { Mutations.SetCell(CF, "status", "processing", new BigtableVersion(2000)) });

        result.PredicateMatched.Should().BeTrue();

        // Try again — should fail since latest status is now "processing"
        var result2 = await Client.CheckAndMutateRowAsync(TN, "order#99",
            RowFilters.Chain(
                RowFilters.ColumnQualifierExact("status"),
                RowFilters.CellsPerColumnLimit(1),
                RowFilters.ValueExact("pending")),
            trueMutations: new[] { Mutations.SetCell(CF, "status", "duplicate", new BigtableVersion(3000)) });

        result2.PredicateMatched.Should().BeFalse();
    }

    [Fact]
    public async Task Batch_import_pattern()
    {
        var entries = Enumerable.Range(0, 100)
            .Select(i => Mutations.CreateEntry($"import#{i:D4}",
                Mutations.SetCell(CF, "name", $"Item {i}", new BigtableVersion(1000)),
                Mutations.SetCell(Metrics, "value", $"{i * 10}", new BigtableVersion(1000))))
            .ToArray();

        await Client.MutateRowsAsync(TN, entries);

        // Verify first and last
        (await Client.ReadRowAsync(TN, "import#0000")).Should().NotBeNull();
        (await Client.ReadRowAsync(TN, "import#0099")).Should().NotBeNull();

        // Count all imported rows
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.RowKeyRegex("import#.*"),
                RowFilters.StripValueTransformer(),
                RowFilters.CellsPerRowLimit(1))
        };
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(100);
    }

    [Fact]
    public async Task Multi_family_read_pattern()
    {
        await Client.MutateRowAsync(TN, "product#123",
            Mutations.SetCell(CF, "name", "Widget", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "price", "9.99", new BigtableVersion(1000)),
            Mutations.SetCell(Metrics, "views", "0", new BigtableVersion(1000)),
            Mutations.SetCell(Metrics, "sales", "0", new BigtableVersion(1000)),
            Mutations.SetCell(Events, "created", "2024-01-01", new BigtableVersion(1000)));

        // Read only profile family
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.FamilyNameExact(CF),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("product#123") } }
        };
        await foreach (var row in Client.ReadRows(request))
        {
            row.Families.Should().HaveCount(1);
            row.Families[0].Name.Should().Be(CF);
        }
    }

    [Fact]
    public async Task TTL_simulation_with_timestamp_filter()
    {
        var now = DateTime.UtcNow;
        var old = now.AddDays(-30);
        var recent = now.AddHours(-1);

        await Client.MutateRowAsync(TN, "cache#item1",
            Mutations.SetCell(CF, "data", "old-value", new BigtableVersion(old)));
        await Client.MutateRowAsync(TN, "cache#item2",
            Mutations.SetCell(CF, "data", "recent-value", new BigtableVersion(recent)));

        // Simulate "only read recent data"
        var cutoff = now.AddDays(-7);
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.RowKeyRegex("cache#.*"),
                RowFilters.TimestampRange(cutoff, null)),
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());

        keys.Should().ContainSingle("cache#item2");
    }

    [Fact]
    public async Task Versioned_configuration_pattern()
    {
        // Write config versions
        await Client.MutateRowAsync(TN, "config#app",
            Mutations.SetCell(CF, "timeout", "30", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "config#app",
            Mutations.SetCell(CF, "timeout", "60", new BigtableVersion(2000)));
        await Client.MutateRowAsync(TN, "config#app",
            Mutations.SetCell(CF, "timeout", "45", new BigtableVersion(3000)));

        // Read latest only
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.CellsPerColumnLimit(1),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("config#app") } }
        };
        await foreach (var row in Client.ReadRows(request))
        {
            var timeout = row.Families.First(f => f.Name == CF).Columns
                .First(c => c.Qualifier.ToStringUtf8() == "timeout")
                .Cells[0].Value.ToStringUtf8();
            timeout.Should().Be("45");
        }
    }

    [Fact]
    public async Task Audit_log_pattern()
    {
        // Append to audit log
        for (int i = 0; i < 5; i++)
            await Client.ReadModifyWriteRowAsync(TN, "audit#user123",
                ReadModifyWriteRules.Append(Events, "log", $"[action{i}]"));

        var row = await Client.ReadRowAsync(TN, "audit#user123");
        var log = row!.Families.First(f => f.Name == Events).Columns
            .First(c => c.Qualifier.ToStringUtf8() == "log")
            .Cells[0].Value.ToStringUtf8();
        log.Should().Contain("[action0]");
        log.Should().Contain("[action4]");
    }

    [Fact]
    public async Task Wide_column_timeseries_pattern()
    {
        // Use column qualifiers as timestamps
        for (int min = 0; min < 60; min++)
        {
            await Client.MutateRowAsync(TN, "sensor#temp#20240615",
                Mutations.SetCell(Metrics, $"min{min:D2}",
                    $"{20.0 + min * 0.1:F1}", new BigtableVersion(1000)));
        }

        // Read specific range of minutes
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.FamilyNameExact(Metrics),
                RowFilters.ColumnRange(ColumnRange.Closed(Metrics, "min10", "min20"))),
            Rows = new RowSet { RowKeys = { ByteString.CopyFromUtf8("sensor#temp#20240615") } }
        };
        var cols = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            foreach (var f in row.Families)
            foreach (var c in f.Columns)
                cols.Add(c.Qualifier.ToStringUtf8());

        cols.Should().HaveCount(11); // min10 through min20 inclusive
    }

    [Fact]
    public async Task Multi_row_lookup_pattern()
    {
        // Create related rows
        await Client.MutateRowAsync(TN, "order#100",
            Mutations.SetCell(CF, "customer", "cust#10", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "order#101",
            Mutations.SetCell(CF, "customer", "cust#10", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "order#102",
            Mutations.SetCell(CF, "customer", "cust#20", new BigtableVersion(1000)));

        // Fetch multiple specific orders
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowKeys =
                {
                    ByteString.CopyFromUtf8("order#100"),
                    ByteString.CopyFromUtf8("order#101"),
                    ByteString.CopyFromUtf8("order#102")
                }
            }
        };
        var count = 0;
        await foreach (var _ in Client.ReadRows(request))
            count++;
        count.Should().Be(3);
    }

    [Fact]
    public async Task Sparse_data_pattern()
    {
        // Different rows have different columns
        await Client.MutateRowAsync(TN, "sparse#1",
            Mutations.SetCell(CF, "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "b", "v2", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "sparse#2",
            Mutations.SetCell(CF, "b", "v3", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v4", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "sparse#3",
            Mutations.SetCell(CF, "a", "v5", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "c", "v6", new BigtableVersion(1000)));

        // Read column "b" across all sparse rows
        var request = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Filter = RowFilters.Chain(
                RowFilters.RowKeyRegex("sparse#.*"),
                RowFilters.ColumnQualifierExact("b"))
        };
        var keys = new List<string>();
        await foreach (var row in Client.ReadRows(request))
            keys.Add(row.Key.ToStringUtf8());

        keys.Should().HaveCount(2);
        keys.Should().Contain("sparse#1");
        keys.Should().Contain("sparse#2");
    }
}
