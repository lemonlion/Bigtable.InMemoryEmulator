using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for row key binary patterns: every byte value, sort order, boundary keys.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#readrowsrequest
///   "Row keys are sorted lexicographically by raw bytes."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class RowKeyBinaryPatternTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public RowKeyBinaryPatternTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync("rk-bin", new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private BigtableServiceApiClient ServiceApi => _fixture.ServiceApiClient;
    private TableName TN => _fixture.GetTableName("rk-bin");

    [Fact]
    public async Task Single_null_byte_row_key()
    {
        var key = ByteString.CopyFrom(0x00);
        var request = new MutateRowRequest
        {
            TableNameAsTableName = TN,
            RowKey = key,
        };
        request.Mutations.Add(new Mutation
        {
            SetCell = new Mutation.Types.SetCell
            {
                FamilyName = CF, ColumnQualifier = ByteString.CopyFromUtf8("c"),
                Value = ByteString.CopyFromUtf8("v"), TimestampMicros = 1_000_000
            }
        });
        await ServiceApi.MutateRowAsync(request);

        // Read it back
        var readReq = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet { RowKeys = { key } }
        };
        var rowKeys = new List<ByteString>();
        await foreach (var response in ServiceApi.ReadRows(readReq).GetResponseStream())
            foreach (var chunk in response.Chunks)
                if (chunk.CommitRow && chunk.RowKey != null && !chunk.RowKey.IsEmpty)
                    rowKeys.Add(chunk.RowKey);

        // The ReadRows response may have separate row key assignment
        // Just verify we can read the row
        var readReq2 = new ReadRowsRequest { TableNameAsTableName = TN };
        var found = false;
        await foreach (var response in ServiceApi.ReadRows(readReq2).GetResponseStream())
            foreach (var chunk in response.Chunks)
                if (chunk.RowKey != null && chunk.RowKey.Length == 1 && chunk.RowKey[0] == 0x00)
                    found = true;
        found.Should().BeTrue();
    }

    [Fact]
    public async Task Row_key_with_0xFF_bytes()
    {
        var key = ByteString.CopyFrom(0xFF, 0xFF, 0xFF);
        var request = new MutateRowRequest
        {
            TableNameAsTableName = TN,
            RowKey = key,
        };
        request.Mutations.Add(new Mutation
        {
            SetCell = new Mutation.Types.SetCell
            {
                FamilyName = CF, ColumnQualifier = ByteString.CopyFromUtf8("c"),
                Value = ByteString.CopyFromUtf8("v"), TimestampMicros = 1_000_000
            }
        });
        await ServiceApi.MutateRowAsync(request);

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys(new BigtableByteString(key))))
            rows.Add(row);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Row_keys_sorted_by_raw_bytes()
    {
        // Write keys that sort differently in UTF-8 vs raw bytes
        var keys = new[]
        {
            ByteString.CopyFrom(0x01),
            ByteString.CopyFrom(0x02),
            ByteString.CopyFrom(0x0A),
            ByteString.CopyFrom(0x10),
            ByteString.CopyFrom(0xFF)
        };

        foreach (var key in keys)
        {
            var request = new MutateRowRequest
            {
                TableNameAsTableName = TN,
                RowKey = key,
            };
            request.Mutations.Add(new Mutation
            {
                SetCell = new Mutation.Types.SetCell
                {
                    FamilyName = CF, ColumnQualifier = ByteString.CopyFromUtf8("c"),
                    Value = ByteString.CopyFromUtf8("v"), TimestampMicros = 1_000_000
                }
            });
            await ServiceApi.MutateRowAsync(request);
        }

        // Read all with range [0x01, 0xFF]
        var readReq = new ReadRowsRequest
        {
            TableNameAsTableName = TN,
            Rows = new RowSet
            {
                RowRanges =
                {
                    new RowRange
                    {
                        StartKeyClosed = ByteString.CopyFrom(0x01),
                        EndKeyClosed = ByteString.CopyFrom(0xFF)
                    }
                }
            }
        };

        var rowKeys = new List<byte[]>();
        byte[]? currentKeyBytes = null;
        await foreach (var response in ServiceApi.ReadRows(readReq).GetResponseStream())
        {
            foreach (var chunk in response.Chunks)
            {
                if (chunk.RowKey != null && !chunk.RowKey.IsEmpty)
                    currentKeyBytes = chunk.RowKey.ToByteArray();
                if (chunk.CommitRow && currentKeyBytes != null)
                {
                    rowKeys.Add(currentKeyBytes);
                    currentKeyBytes = null;
                }
            }
        }

        // Verify ascending byte order
        for (int i = 1; i < rowKeys.Count; i++)
        {
            var cmp = CompareBytes(rowKeys[i - 1], rowKeys[i]);
            cmp.Should().BeLessThan(0, $"Key at {i - 1} should be less than key at {i}");
        }
    }

    private static int CompareBytes(byte[] a, byte[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        }
        return a.Length.CompareTo(b.Length);
    }

    [Fact]
    public async Task Row_key_max_length_4096_succeeds()
    {
        var key = ByteString.CopyFrom(Enumerable.Repeat((byte)0x42, 4096).ToArray());
        var request = new MutateRowRequest
        {
            TableNameAsTableName = TN,
            RowKey = key,
        };
        request.Mutations.Add(new Mutation
        {
            SetCell = new Mutation.Types.SetCell
            {
                FamilyName = CF, ColumnQualifier = ByteString.CopyFromUtf8("c"),
                Value = ByteString.CopyFromUtf8("v"), TimestampMicros = 1_000_000
            }
        });
        await ServiceApi.MutateRowAsync(request);
    }

    [Fact]
    public async Task Row_key_with_mixed_binary_and_text()
    {
        var key = ByteString.CopyFrom(
            new byte[] { 0x00, 0x01 }
                .Concat(System.Text.Encoding.UTF8.GetBytes("user#123"))
                .Concat(new byte[] { 0xFF }).ToArray());

        var request = new MutateRowRequest
        {
            TableNameAsTableName = TN,
            RowKey = key,
        };
        request.Mutations.Add(new Mutation
        {
            SetCell = new Mutation.Types.SetCell
            {
                FamilyName = CF, ColumnQualifier = ByteString.CopyFromUtf8("c"),
                Value = ByteString.CopyFromUtf8("v"), TimestampMicros = 1_000_000
            }
        });
        await ServiceApi.MutateRowAsync(request);

        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys(new BigtableByteString(key))))
            rows.Add(row);
        rows.Should().ContainSingle();
    }

    [Fact]
    public async Task Row_key_single_byte()
    {
        await Client.MutateRowAsync(TN, "x",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        var row = await Client.ReadRowAsync(TN, "x");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Row_key_with_separator_pattern()
    {
        // Common pattern: prefix#id#suffix
        await Client.MutateRowAsync(TN, "user#123#profile",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "user#123#settings",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));
        await Client.MutateRowAsync(TN, "user#456#profile",
            Mutations.SetCell(CF, "c", "v", new BigtableVersion(1000)));

        // Range scan for user#123
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN,
            new RowSet
            {
                RowRanges =
                {
                    RowRange.ClosedOpen("user#123#", "user#123$")
                }
            }))
            rows.Add(row);
        rows.Should().HaveCount(2);
    }
}
