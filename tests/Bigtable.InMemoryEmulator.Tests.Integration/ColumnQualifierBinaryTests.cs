using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for binary column qualifiers and empty qualifiers.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#mutation
///   "column_qualifier: The qualifier of the column into which new data should be written."
///   Column qualifiers can contain any bytes.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ColumnQualifierBinaryTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string CF = "cf";

    public ColumnQualifierBinaryTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync("col-qual-bin", new[] { CF });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName("col-qual-bin");

    [Fact]
    public async Task Empty_column_qualifier()
    {
        var qualifier = ByteString.Empty;
        var mutation = new Mutation
        {
            SetCell = new Mutation.Types.SetCell
            {
                FamilyName = CF,
                ColumnQualifier = qualifier,
                Value = ByteString.CopyFromUtf8("v"),
                TimestampMicros = 1_000_000
            }
        };
        var request = new MutateRowRequest
        {
            TableNameAsTableName = TN,
            RowKey = ByteString.CopyFromUtf8("empty-qual"),
        };
        request.Mutations.Add(mutation);
        await _fixture.ServiceApiClient.MutateRowAsync(request);

        var row = await Client.ReadRowAsync(TN, "empty-qual");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Qualifier.Should().BeEmpty();
    }

    [Fact]
    public async Task Binary_qualifier_with_null_bytes()
    {
        var qualifier = ByteString.CopyFrom(0x00, 0x01, 0x02, 0x00);
        var mutation = new Mutation
        {
            SetCell = new Mutation.Types.SetCell
            {
                FamilyName = CF,
                ColumnQualifier = qualifier,
                Value = ByteString.CopyFromUtf8("v"),
                TimestampMicros = 1_000_000
            }
        };
        var request = new MutateRowRequest
        {
            TableNameAsTableName = TN,
            RowKey = ByteString.CopyFromUtf8("binary-null-qual"),
        };
        request.Mutations.Add(mutation);
        await _fixture.ServiceApiClient.MutateRowAsync(request);

        var row = await Client.ReadRowAsync(TN, "binary-null-qual");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Qualifier.ToByteArray().Should().BeEquivalentTo(new byte[] { 0, 1, 2, 0 });
    }

    [Fact]
    public async Task Binary_qualifier_all_byte_values()
    {
        // Test with bytes 0-255
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var qualifier = ByteString.CopyFrom(bytes);
        var mutation = new Mutation
        {
            SetCell = new Mutation.Types.SetCell
            {
                FamilyName = CF,
                ColumnQualifier = qualifier,
                Value = ByteString.CopyFromUtf8("v"),
                TimestampMicros = 1_000_000
            }
        };
        var request = new MutateRowRequest
        {
            TableNameAsTableName = TN,
            RowKey = ByteString.CopyFromUtf8("binary-all-qual"),
        };
        request.Mutations.Add(mutation);
        await _fixture.ServiceApiClient.MutateRowAsync(request);

        var row = await Client.ReadRowAsync(TN, "binary-all-qual");
        row.Should().NotBeNull();
        row!.Families[0].Columns[0].Qualifier.ToByteArray().Should().BeEquivalentTo(bytes);
    }

    [Fact]
    public async Task Long_column_qualifier()
    {
        // Just under 16KiB limit
        var longQual = ByteString.CopyFrom(Enumerable.Repeat((byte)0x41, 16000).ToArray());
        var mutation = new Mutation
        {
            SetCell = new Mutation.Types.SetCell
            {
                FamilyName = CF,
                ColumnQualifier = longQual,
                Value = ByteString.CopyFromUtf8("v"),
                TimestampMicros = 1_000_000
            }
        };
        var request = new MutateRowRequest
        {
            TableNameAsTableName = TN,
            RowKey = ByteString.CopyFromUtf8("long-qual"),
        };
        request.Mutations.Add(mutation);
        await _fixture.ServiceApiClient.MutateRowAsync(request);

        var row = await Client.ReadRowAsync(TN, "long-qual");
        row.Should().NotBeNull();
    }

    [Fact]
    public async Task Multiple_binary_qualifiers_in_same_family()
    {
        var q1 = ByteString.CopyFrom(0x01, 0x02);
        var q2 = ByteString.CopyFrom(0x03, 0x04);
        var q3 = ByteString.CopyFrom(0x05);
        var request = new MutateRowRequest
        {
            TableNameAsTableName = TN,
            RowKey = ByteString.CopyFromUtf8("multi-bin-qual"),
        };
        request.Mutations.Add(new Mutation
        {
            SetCell = new Mutation.Types.SetCell
            {
                FamilyName = CF, ColumnQualifier = q1,
                Value = ByteString.CopyFromUtf8("v1"), TimestampMicros = 1_000_000
            }
        });
        request.Mutations.Add(new Mutation
        {
            SetCell = new Mutation.Types.SetCell
            {
                FamilyName = CF, ColumnQualifier = q2,
                Value = ByteString.CopyFromUtf8("v2"), TimestampMicros = 1_000_000
            }
        });
        request.Mutations.Add(new Mutation
        {
            SetCell = new Mutation.Types.SetCell
            {
                FamilyName = CF, ColumnQualifier = q3,
                Value = ByteString.CopyFromUtf8("v3"), TimestampMicros = 1_000_000
            }
        });
        await _fixture.ServiceApiClient.MutateRowAsync(request);

        var row = await Client.ReadRowAsync(TN, "multi-bin-qual");
        row.Should().NotBeNull();
        row!.Families[0].Columns.Should().HaveCount(3);
    }

    [Fact]
    public async Task Qualifier_with_utf8_special_chars()
    {
        await Client.MutateRowAsync(TN, "utf8-qual",
            Mutations.SetCell(CF, "日本語", "v", new BigtableVersion(1000)),
            Mutations.SetCell(CF, "émoji🎉", "v2", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, "utf8-qual");
        row.Should().NotBeNull();
        var qualifiers = row!.Families[0].Columns.Select(c => c.Qualifier.ToStringUtf8()).ToList();
        qualifiers.Should().Contain("日本語");
        qualifiers.Should().Contain("émoji🎉");
    }

    [Fact]
    public async Task Column_qualifier_regex_filter_on_binary()
    {
        var q = ByteString.CopyFrom(0xAA, 0xBB, 0xCC);
        var request = new MutateRowRequest
        {
            TableNameAsTableName = TN,
            RowKey = ByteString.CopyFromUtf8("regex-bin-qual"),
        };
        request.Mutations.Add(new Mutation
        {
            SetCell = new Mutation.Types.SetCell
            {
                FamilyName = CF, ColumnQualifier = q,
                Value = ByteString.CopyFromUtf8("v"), TimestampMicros = 1_000_000
            }
        });
        await _fixture.ServiceApiClient.MutateRowAsync(request);

        // Filter should find it using exact qualifier
        var filter = new RowFilter
        {
            ColumnQualifierRegexFilter = q
        };
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys("regex-bin-qual"), filter))
            rows.Add(row);
        rows.Should().ContainSingle();
    }
}
