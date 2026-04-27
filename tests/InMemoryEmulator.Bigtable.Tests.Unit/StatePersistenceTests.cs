using InMemoryEmulator.Bigtable;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Tests for state persistence: ExportState, ImportState, file I/O.
/// </summary>
public class StatePersistenceTests : IDisposable
{
    private readonly string _tempDir;

    public StatePersistenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bigtable-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task ExportState_returns_json_with_all_data()
    {
        using var result = InMemoryBigtable.Create("t1", ["cf1"]);
        var tableName = result.GetTableName("t1");
        var client = result.Client;

        await client.MutateRowAsync(tableName, new BigtableByteString("row1"),
            Mutations.SetCell("cf1", "col", "value1", new BigtableVersion(1000)));
        await client.MutateRowAsync(tableName, new BigtableByteString("row2"),
            Mutations.SetCell("cf1", "col", "value2", new BigtableVersion(2000)));

        var json = result.ExportState();

        json.Should().Contain("t1");
        json.Should().Contain("cf1");
    }

    [Fact]
    public async Task ImportState_restores_data_from_export()
    {
        // Export from one instance
        string exportedJson;
        using (var result1 = InMemoryBigtable.Create("t1", ["cf1"]))
        {
            var tableName = result1.GetTableName("t1");
            await result1.Client.MutateRowAsync(tableName, new BigtableByteString("row1"),
                Mutations.SetCell("cf1", "col", "hello", new BigtableVersion(1000)));
            await result1.Client.MutateRowAsync(tableName, new BigtableByteString("row2"),
                Mutations.SetCell("cf1", "col", "world", new BigtableVersion(2000)));

            exportedJson = result1.ExportState();
        }

        // Import into a fresh instance
        using var result2 = InMemoryBigtable.Create("t1", ["cf1"]);
        result2.ImportState(exportedJson);

        var tableName2 = result2.GetTableName("t1");
        var row1 = await result2.Client.ReadRowAsync(tableName2, new BigtableByteString("row1"));
        var row2 = await result2.Client.ReadRowAsync(tableName2, new BigtableByteString("row2"));

        row1.Should().NotBeNull();
        row2.Should().NotBeNull();
        row1!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("hello");
        row2!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("world");
    }

    [Fact]
    public async Task ImportState_replaces_existing_data()
    {
        using var result = InMemoryBigtable.Create("t1", ["cf1"]);
        var tableName = result.GetTableName("t1");

        // Write initial data
        await result.Client.MutateRowAsync(tableName, new BigtableByteString("old-row"),
            Mutations.SetCell("cf1", "col", "old-value", new BigtableVersion(1000)));

        // Export with different data
        string json;
        using (var tmp = InMemoryBigtable.Create("t1", ["cf1"]))
        {
            await tmp.Client.MutateRowAsync(tmp.GetTableName("t1"), new BigtableByteString("new-row"),
                Mutations.SetCell("cf1", "col", "new-value", new BigtableVersion(2000)));
            json = tmp.ExportState();
        }

        result.ImportState(json);

        // Old data should be gone
        var oldRow = await result.Client.ReadRowAsync(tableName, new BigtableByteString("old-row"));
        oldRow.Should().BeNull();

        // New data should be present
        var newRow = await result.Client.ReadRowAsync(tableName, new BigtableByteString("new-row"));
        newRow.Should().NotBeNull();
        newRow!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("new-value");
    }

    [Fact]
    public async Task ExportStateToFile_and_ImportStateFromFile_round_trip()
    {
        var filePath = Path.Combine(_tempDir, "state.json");

        // Export
        using (var result1 = InMemoryBigtable.Create("t1", ["cf1"]))
        {
            await result1.Client.MutateRowAsync(result1.GetTableName("t1"), new BigtableByteString("row1"),
                Mutations.SetCell("cf1", "col", "persisted", new BigtableVersion(1000)));
            result1.ExportStateToFile(filePath);
        }

        File.Exists(filePath).Should().BeTrue();

        // Import
        using var result2 = InMemoryBigtable.Create("t1", ["cf1"]);
        result2.ImportStateFromFile(filePath);

        var row = await result2.Client.ReadRowAsync(result2.GetTableName("t1"), new BigtableByteString("row1"));
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("persisted");
    }

    [Fact]
    public async Task Export_preserves_multiple_versions()
    {
        using var result = InMemoryBigtable.Create("t1", ["cf1"]);
        var tableName = result.GetTableName("t1");

        await result.Client.MutateRowAsync(tableName, new BigtableByteString("row1"),
            Mutations.SetCell("cf1", "col", "v1", new BigtableVersion(1000)));
        await result.Client.MutateRowAsync(tableName, new BigtableByteString("row1"),
            Mutations.SetCell("cf1", "col", "v2", new BigtableVersion(2000)));

        var json = result.ExportState();

        using var result2 = InMemoryBigtable.Create("t1", ["cf1"]);
        result2.ImportState(json);

        var row = await result2.Client.ReadRowAsync(result2.GetTableName("t1"), new BigtableByteString("row1"));
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Cells.Should().HaveCount(2);
    }

    [Fact]
    public async Task Export_preserves_binary_values()
    {
        using var result = InMemoryBigtable.Create("t1", ["cf1"]);
        var tableName = result.GetTableName("t1");
        var binaryValue = ByteString.CopyFrom(new byte[] { 0x00, 0xFF, 0x80, 0x01 });

        await result.Client.MutateRowAsync(tableName, new BigtableByteString("row1"),
            Mutations.SetCell("cf1", ByteString.CopyFromUtf8("col"), binaryValue, new BigtableVersion(1000)));

        var json = result.ExportState();

        using var result2 = InMemoryBigtable.Create("t1", ["cf1"]);
        result2.ImportState(json);

        var row = await result2.Client.ReadRowAsync(result2.GetTableName("t1"), new BigtableByteString("row1"));
        row.Should().NotBeNull();
        var cellValue = row!.Families[0].Columns[0].Cells[0].Value;
        cellValue.Span.ToArray().Should().BeEquivalentTo(new byte[] { 0x00, 0xFF, 0x80, 0x01 });
    }
}
