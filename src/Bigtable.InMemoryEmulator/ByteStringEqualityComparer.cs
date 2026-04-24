using Google.Protobuf;

namespace Bigtable.InMemoryEmulator;

/// <summary>
/// Equality comparer for ByteString using value equality (byte-by-byte comparison).
/// Used in HashSet and Dictionary for row key lookups.
/// </summary>
internal sealed class ByteStringEqualityComparer : IEqualityComparer<ByteString>
{
    public static readonly ByteStringEqualityComparer Instance = new();

    private ByteStringEqualityComparer() { }

    public bool Equals(ByteString? x, ByteString? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        return x.Span.SequenceEqual(y.Span);
    }

    public int GetHashCode(ByteString obj)
    {
        if (obj is null) return 0;

        // FNV-1a hash over bytes
        unchecked
        {
            int hash = (int)2166136261;
            foreach (var b in obj.Span)
            {
                hash = (hash ^ b) * 16777619;
            }
            return hash;
        }
    }
}
