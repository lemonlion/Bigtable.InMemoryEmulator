using Google.Protobuf;

namespace InMemoryEmulator.Bigtable;

/// <summary>
/// Represents a single cell in a Bigtable row.
/// A cell is uniquely identified by (family, qualifier, timestamp) within a row.
/// </summary>
internal sealed class CellData
{
    public required string Family { get; init; }
    public required ByteString Qualifier { get; init; }
    public required long TimestampMicros { get; init; }
    public ByteString Value { get; set; } = ByteString.Empty;
    public List<string> Labels { get; } = [];
}
