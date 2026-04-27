using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class ConditionalMutationIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "conditional-tests";
    private const string Family = "cf";

    public ConditionalMutationIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();
    public async ValueTask InitializeAsync() => await _fixture.CreateTableAsync(Table, new[] { Family });
    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    [Fact]
    public async Task CheckAndMutateRow_applies_true_mutations_when_predicate_matches()
    {
        var rowKey = new BigtableByteString("cam-true");
        await Client.MutateRowAsync(TN, rowKey,
            Mutations.SetCell(Family, "col", "existing", new BigtableVersion(1000)));
        var response = await Client.CheckAndMutateRowAsync(TN, rowKey,
            RowFilters.PassAllFilter(),
            trueMutations: new[] { Mutations.SetCell(Family, "col2", "matched", new BigtableVersion(2000)) },
            falseMutations: null);
        response.PredicateMatched.Should().BeTrue();
        var row = await Client.ReadRowAsync(TN, rowKey);
        row!.Families[0].Columns.Should().Contain(c => c.Qualifier.ToStringUtf8() == "col2");
    }

    [Fact]
    public async Task CheckAndMutateRow_applies_false_mutations_when_no_match()
    {
        var rowKey = new BigtableByteString("cam-false");
        var response = await Client.CheckAndMutateRowAsync(TN, rowKey,
            RowFilters.PassAllFilter(),
            trueMutations: null,
            falseMutations: new[] { Mutations.SetCell(Family, "col", "no-match", new BigtableVersion(1000)) });
        response.PredicateMatched.Should().BeFalse();
        var row = await Client.ReadRowAsync(TN, rowKey);
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("no-match");
    }

    [Fact]
    public async Task ReadModifyWriteRow_increments_value()
    {
        var rowKey = new BigtableByteString("rmw-inc");
        var response = await Client.ReadModifyWriteRowAsync(TN, rowKey,
            ReadModifyWriteRules.Increment(Family, "counter", 5));
        response.Should().NotBeNull();
        var row = await Client.ReadRowAsync(TN, rowKey);
        row.Should().NotBeNull();
        var bytes = row!.Families[0].Columns[0].Cells[0].Value.ToByteArray();
        var value = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(bytes);
        value.Should().Be(5);
    }

    [Fact]
    public async Task ReadModifyWriteRow_accumulates_increments()
    {
        var rowKey = new BigtableByteString("rmw-acc");
        await Client.ReadModifyWriteRowAsync(TN, rowKey,
            ReadModifyWriteRules.Increment(Family, "counter", 10));
        await Client.ReadModifyWriteRowAsync(TN, rowKey,
            ReadModifyWriteRules.Increment(Family, "counter", 7));
        var row = await Client.ReadRowAsync(TN, rowKey);
        var bytes = row!.Families[0].Columns[0].Cells[0].Value.ToByteArray();
        var value = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(bytes);
        value.Should().Be(17);
    }

    [Fact]
    public async Task ReadModifyWriteRow_appends_value()
    {
        var rowKey = new BigtableByteString("rmw-app");
        await Client.MutateRowAsync(TN, rowKey,
            Mutations.SetCell(Family, "data", "hello", new BigtableVersion(1000)));
        await Client.ReadModifyWriteRowAsync(TN, rowKey,
            ReadModifyWriteRules.Append(Family, "data", " world"));
        var row = await Client.ReadRowAsync(TN, rowKey);
        row!.Families[0].Columns[0].Cells[0].Value.ToStringUtf8().Should().Be("hello world");
    }
}
