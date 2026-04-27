using Google.Protobuf;

namespace InMemoryEmulator.Bigtable;

/// <summary>
/// Compares ByteString values in unsigned lexicographic order (same as Bigtable's row key ordering).
/// Ref: https://cloud.google.com/bigtable/docs/reference/data/rpc/google.bigtable.v2#row
///   "Row keys sort lexicographically by raw bytes."
/// </summary>
internal sealed class ByteStringComparer : IComparer<ByteString>
{
    public static readonly ByteStringComparer Instance = new();

    private ByteStringComparer() { }

    public int Compare(ByteString? x, ByteString? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var spanX = x.Span;
        var spanY = y.Span;
        var minLength = Math.Min(spanX.Length, spanY.Length);

        for (int i = 0; i < minLength; i++)
        {
            int cmp = spanX[i].CompareTo(spanY[i]);
            if (cmp != 0) return cmp;
        }

        return spanX.Length.CompareTo(spanY.Length);
    }
}
