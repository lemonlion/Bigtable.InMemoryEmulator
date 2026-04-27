using Google.Cloud.Bigtable.Admin.V2;
using Google.Cloud.Bigtable.Common.V2;
using Google.Cloud.Bigtable.V2;
using Google.Protobuf;

namespace InMemoryEmulator.Bigtable.Tests;

/// <summary>
/// Multi-family integration tests — reads/writes spanning multiple column families,
/// family-level filtering, and family ordering.
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#row
///   "Families are sorted in ascending order by family name."
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait(TestTraits.Target, TestTraits.All)]
public sealed class MultiFamilyIntegrationTests : IAsyncLifetime
{
    private readonly ITestTableFixture _fixture;
    private const string Table = "multi-fam-tests";

    public MultiFamilyIntegrationTests(EmulatorSession session) => _fixture = session.CreateFixture();

    public async ValueTask InitializeAsync()
    {
        await _fixture.CreateTableAsync(Table, new[] { "alpha", "beta", "gamma", "delta" });
    }

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();
    private BigtableClient Client => _fixture.Client;
    private TableName TN => _fixture.GetTableName(Table);

    #region Write and read across families

    [Fact]
    public async Task Write_to_all_families_read_back()
    {
        var rk = new BigtableByteString("mf-all");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("alpha", "c", "a", new BigtableVersion(1000)),
            Mutations.SetCell("beta", "c", "b", new BigtableVersion(1000)),
            Mutations.SetCell("gamma", "c", "g", new BigtableVersion(1000)),
            Mutations.SetCell("delta", "c", "d", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.Should().HaveCount(4);
    }

    [Fact]
    public async Task Families_returned_in_ascending_name_order()
    {
        // Ref: "Families are sorted in ascending order by family name."
        var rk = new BigtableByteString("mf-order");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("gamma", "c", "g", new BigtableVersion(1000)),
            Mutations.SetCell("alpha", "c", "a", new BigtableVersion(1000)),
            Mutations.SetCell("delta", "c", "d", new BigtableVersion(1000)),
            Mutations.SetCell("beta", "c", "b", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        var familyNames = row!.Families.Select(f => f.Name).ToList();
        familyNames.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Each_family_has_independent_columns()
    {
        var rk = new BigtableByteString("mf-indep");
        // Same column qualifier in different families
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("alpha", "same-col", "alpha-val", new BigtableVersion(1000)),
            Mutations.SetCell("beta", "same-col", "beta-val", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        var alphaVal = row!.Families.First(f => f.Name == "alpha")
            .Columns.First().Cells[0].Value.ToStringUtf8();
        var betaVal = row.Families.First(f => f.Name == "beta")
            .Columns.First().Cells[0].Value.ToStringUtf8();
        alphaVal.Should().Be("alpha-val");
        betaVal.Should().Be("beta-val");
    }

    [Fact]
    public async Task Delete_from_one_family_preserves_others()
    {
        var rk = new BigtableByteString("mf-del-one");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("alpha", "c", "a", new BigtableVersion(1000)),
            Mutations.SetCell("beta", "c", "b", new BigtableVersion(1000)),
            Mutations.SetCell("gamma", "c", "g", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromFamily("beta"));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.Should().HaveCount(2);
        row.Families.Select(f => f.Name).Should().Contain("alpha");
        row.Families.Select(f => f.Name).Should().Contain("gamma");
        row.Families.Select(f => f.Name).Should().NotContain("beta");
    }

    [Fact]
    public async Task Delete_from_column_in_specific_family()
    {
        var rk = new BigtableByteString("mf-del-col");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("alpha", "c", "a", new BigtableVersion(1000)),
            Mutations.SetCell("beta", "c", "b", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromColumn("alpha", "c"));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.Should().Contain(f => f.Name == "beta");
        row.Families.Should().NotContain(f => f.Name == "alpha");
    }

    #endregion

    #region Family filtering

    [Fact]
    public async Task FamilyNameRegex_selects_matching_families()
    {
        var rk = new BigtableByteString("mf-fnr");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("alpha", "c", "a", new BigtableVersion(1000)),
            Mutations.SetCell("beta", "c", "b", new BigtableVersion(1000)),
            Mutations.SetCell("gamma", "c", "g", new BigtableVersion(1000)),
            Mutations.SetCell("delta", "c", "d", new BigtableVersion(1000)));

        var filter = RowFilters.FamilyNameRegex("alpha|beta");
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys(rk), filter: filter))
        {
            rows.Add(row);
        }
        rows.Should().ContainSingle();
        rows[0].Families.Select(f => f.Name).Should().BeEquivalentTo("alpha", "beta");
    }

    [Fact]
    public async Task FamilyNameRegex_exact_match()
    {
        var rk = new BigtableByteString("mf-fnr-exact");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("alpha", "c", "a", new BigtableVersion(1000)),
            Mutations.SetCell("beta", "c", "b", new BigtableVersion(1000)));

        var filter = RowFilters.FamilyNameRegex("alpha");
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys(rk), filter: filter))
        {
            rows.Add(row);
        }
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be("alpha");
    }

    [Fact]
    public async Task FamilyNameRegex_no_match_returns_empty()
    {
        var rk = new BigtableByteString("mf-fnr-none");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("alpha", "c", "a", new BigtableVersion(1000)));

        var filter = RowFilters.FamilyNameRegex("zzz_nonexistent");
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys(rk), filter: filter))
        {
            rows.Add(row);
        }
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Chain_family_and_column_filter()
    {
        var rk = new BigtableByteString("mf-chain-fc");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("alpha", "keep", "yes", new BigtableVersion(1000)),
            Mutations.SetCell("alpha", "drop", "no", new BigtableVersion(1000)),
            Mutations.SetCell("beta", "keep", "also", new BigtableVersion(1000)));

        var filter = RowFilters.Chain(
            RowFilters.FamilyNameRegex("alpha"),
            RowFilters.ColumnQualifierRegex("keep"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys(rk), filter: filter))
        {
            rows.Add(row);
        }
        rows.Should().ContainSingle();
        rows[0].Families.Should().ContainSingle().Which.Name.Should().Be("alpha");
        rows[0].Families[0].Columns.Should().ContainSingle()
            .Which.Qualifier.ToStringUtf8().Should().Be("keep");
    }

    #endregion

    #region Multiple families with versions

    [Fact]
    public async Task Each_family_has_independent_version_history()
    {
        var rk = new BigtableByteString("mf-versions");
        // Write 3 versions to alpha, 1 to beta
        for (int i = 1; i <= 3; i++)
        {
            await Client.MutateRowAsync(TN, rk,
                Mutations.SetCell("alpha", "c", $"a{i}", new BigtableVersion(i * 1000)));
        }
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("beta", "c", "b1", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.First(f => f.Name == "alpha").Columns[0].Cells.Should().HaveCount(3);
        row.Families.First(f => f.Name == "beta").Columns[0].Cells.Should().HaveCount(1);
    }

    [Fact]
    public async Task Delete_family_removes_all_columns_and_versions()
    {
        var rk = new BigtableByteString("mf-del-fam");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("alpha", "a", "v1", new BigtableVersion(1000)),
            Mutations.SetCell("alpha", "b", "v2", new BigtableVersion(1000)),
            Mutations.SetCell("alpha", "c", "v3", new BigtableVersion(1000)),
            Mutations.SetCell("beta", "c", "keeper", new BigtableVersion(1000)));

        await Client.MutateRowAsync(TN, rk,
            Mutations.DeleteFromFamily("alpha"));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.Should().ContainSingle().Which.Name.Should().Be("beta");
    }

    [Fact]
    public async Task Write_to_empty_family_then_read()
    {
        // Writing to a family that exists but has no data
        var rk = new BigtableByteString("mf-empty-then");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("delta", "first", "val", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row.Should().NotBeNull();
        row!.Families.Should().ContainSingle().Which.Name.Should().Be("delta");
    }

    #endregion

    #region Interleave filter across families

    [Fact]
    public async Task Interleave_family_filters()
    {
        var rk = new BigtableByteString("mf-interleave");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("alpha", "c", "a", new BigtableVersion(1000)),
            Mutations.SetCell("beta", "c", "b", new BigtableVersion(1000)),
            Mutations.SetCell("gamma", "c", "g", new BigtableVersion(1000)));

        var filter = RowFilters.Interleave(
            RowFilters.FamilyNameRegex("alpha"),
            RowFilters.FamilyNameRegex("gamma"));
        var rows = new List<Row>();
        await foreach (var row in Client.ReadRows(TN, RowSet.FromRowKeys(rk), filter: filter))
        {
            rows.Add(row);
        }
        rows.Should().ContainSingle();
        rows[0].Families.Select(f => f.Name).Should().BeEquivalentTo("alpha", "gamma");
    }

    #endregion

    #region Add/Drop families after data

    [Fact]
    public async Task Add_family_then_write_to_new_family()
    {
        var tablePath = _fixture.InstanceName + "/tables/" + Table;
        await _fixture.AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = tablePath,
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "newfam",
                    Create = new ColumnFamily()
                }
            }
        });

        var rk = new BigtableByteString("mf-newfam");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("newfam", "c", "newval", new BigtableVersion(1000)));

        var row = await Client.ReadRowAsync(TN, rk);
        row!.Families.Should().Contain(f => f.Name == "newfam");
    }

    [Fact]
    public async Task Drop_family_removes_data_in_that_family()
    {
        var rk = new BigtableByteString("mf-drop");
        await Client.MutateRowAsync(TN, rk,
            Mutations.SetCell("alpha", "c", "a", new BigtableVersion(1000)),
            Mutations.SetCell("beta", "c", "b", new BigtableVersion(1000)));

        var tablePath = _fixture.InstanceName + "/tables/" + Table;
        await _fixture.AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = tablePath,
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "alpha",
                    Drop = true
                }
            }
        });

        var row = await Client.ReadRowAsync(TN, rk);
        if (row != null)
        {
            row.Families.Should().NotContain(f => f.Name == "alpha");
        }

        // Re-add alpha for other tests
        await _fixture.AdminClient.ModifyColumnFamiliesAsync(new ModifyColumnFamiliesRequest
        {
            Name = tablePath,
            Modifications =
            {
                new ModifyColumnFamiliesRequest.Types.Modification
                {
                    Id = "alpha",
                    Create = new ColumnFamily()
                }
            }
        });
    }

    #endregion
}
