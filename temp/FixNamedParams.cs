using System.Text.RegularExpressions;

var dir = @"C:\git\InMemoryEmulator.Bigtable\tests\InMemoryEmulator.Bigtable.Tests.Integration";
foreach (var f in Directory.GetFiles(dir, "*.cs"))
{
    var c = File.ReadAllText(f);
    // Fix: ReadRows(TN, <expr>,\n  rows: null, filter: -> ReadRows(TN, <expr>,\n  filter:
    // Only when <expr> is NOT "rows: null" (i.e. there's a positional RowSet before the named rows: null)
    var pattern = @"(ReadRows\(TN,\s+(?!rows:)[^\n]+,)\s*\r?\n(\s+)rows:\s*null,\s*filter:";
    var replacement = "$1\n$2filter:";
    var newC = Regex.Replace(c, pattern, replacement);
    if (newC != c)
    {
        File.WriteAllText(f, newC);
        Console.WriteLine("Fixed: " + Path.GetFileName(f));
    }
}
Console.WriteLine("Done");
