using Bigtable.InMemoryEmulator;
using Grpc.Core;

namespace Bigtable.InMemoryEmulator.Tests;

/// <summary>
/// Tests for the Superpower-based resource name parser.
/// </summary>
public class ResourceNameParserTests
{
    #region Fully-qualified table names

    [Fact]
    public void ParseTableName_extracts_table_from_fully_qualified_name()
    {
        var result = ResourceNameParser.ExtractTableName("projects/my-proj/instances/my-inst/tables/my-table");
        result.Should().Be("my-table");
    }

    [Fact]
    public void ParseTableName_extracts_table_with_hyphens_and_underscores()
    {
        var result = ResourceNameParser.ExtractTableName("projects/p/instances/i/tables/my_table-v2");
        result.Should().Be("my_table-v2");
    }

    [Fact]
    public void ParseTableName_parses_simple_table_name_as_fallback()
    {
        var result = ResourceNameParser.ExtractTableName("simple-name");
        result.Should().Be("simple-name");
    }

    [Fact]
    public void ParseTableName_throws_on_empty_name()
    {
        var act = () => ResourceNameParser.ExtractTableName("");
        act.Should().Throw<RpcException>()
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public void ParseTableName_throws_on_null_name()
    {
        var act = () => ResourceNameParser.ExtractTableName(null!);
        act.Should().Throw<RpcException>()
            .Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    #endregion

    #region Full resource name parsing

    [Fact]
    public void TryParseResourceName_extracts_all_components()
    {
        var parsed = ResourceNameParser.TryParseResourceName("projects/my-proj/instances/my-inst/tables/my-table");

        parsed.Should().NotBeNull();
        parsed!.Value.Project.Should().Be("my-proj");
        parsed.Value.Instance.Should().Be("my-inst");
        parsed.Value.Table.Should().Be("my-table");
    }

    [Fact]
    public void TryParseResourceName_returns_null_for_non_qualified_name()
    {
        var parsed = ResourceNameParser.TryParseResourceName("simple-table");
        parsed.Should().BeNull();
    }

    [Fact]
    public void TryParseResourceName_returns_null_for_malformed_path()
    {
        var parsed = ResourceNameParser.TryParseResourceName("projects/p/instances/i/wrong/t");
        parsed.Should().BeNull();
    }

    [Fact]
    public void TryParseResourceName_handles_dots_in_project()
    {
        var parsed = ResourceNameParser.TryParseResourceName("projects/my.project/instances/inst1/tables/t1");

        parsed.Should().NotBeNull();
        parsed!.Value.Project.Should().Be("my.project");
    }

    #endregion
}
