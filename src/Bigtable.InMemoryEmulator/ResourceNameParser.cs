using Grpc.Core;
using Superpower;
using Superpower.Parsers;

namespace Bigtable.InMemoryEmulator;

/// <summary>
/// Superpower-based parser for Bigtable resource names.
/// Format: "projects/{project}/instances/{instance}/tables/{table}"
///
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2
///   All RPC requests use fully-qualified table resource names.
/// </summary>
internal static class ResourceNameParser
{
    /// <summary>
    /// Parsed components of a fully-qualified Bigtable table resource name.
    /// </summary>
    public readonly record struct ParsedResourceName(string Project, string Instance, string Table);

    // Superpower TextParsers for the resource name format:
    // "projects/{project}/instances/{instance}/tables/{table}"
    private static readonly TextParser<char> Slash = Character.EqualTo('/');
    private static readonly TextParser<string> Segment = Character.Except('/').AtLeastOnce().Select(chars => new string(chars));

    private static readonly TextParser<ParsedResourceName> FullResourceName =
        from _p in Span.EqualTo("projects")
        from _s1 in Slash
        from project in Segment
        from _s2 in Slash
        from _i in Span.EqualTo("instances")
        from _s3 in Slash
        from instance in Segment
        from _s4 in Slash
        from _t in Span.EqualTo("tables")
        from _s5 in Slash
        from table in Segment
        select new ParsedResourceName(project, instance, table);

    /// <summary>
    /// Extracts the short table name from a fully-qualified resource name or returns the input as-is.
    /// Used by both BigtableGrpcService and BigtableTableAdminGrpcService.
    /// </summary>
    public static string ExtractTableName(string resourceName)
    {
        if (string.IsNullOrEmpty(resourceName))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Table name must not be empty."));
        }

        var parsed = TryParseResourceName(resourceName);
        if (parsed.HasValue)
        {
            return parsed.Value.Table;
        }

        // If it's just a table name (not fully-qualified), use as-is
        return resourceName;
    }

    /// <summary>
    /// Attempts to parse a fully-qualified resource name into its components.
    /// Returns null if the format doesn't match.
    /// </summary>
    public static ParsedResourceName? TryParseResourceName(string resourceName)
    {
        var result = FullResourceName.TryParse(resourceName);
        return result.HasValue ? result.Value : null;
    }
}
