using System;
using System.IO;
using System.Text.RegularExpressions;
class Program {
    static void Main() {
        var dir = @"C:\git\InMemoryEmulator.Bigtable\tests\InMemoryEmulator.Bigtable.Tests.Integration";
        foreach (var f in Directory.GetFiles(dir, "*.cs")) {
            var c = File.ReadAllText(f);
            var pattern = @"(ReadRows\(TN,\s+(?!rows:)[^\n]+,)\s*\r?\n(\s+)rows:\s*null,\s*filter:";
            var newC = Regex.Replace(c, pattern, m => m.Groups[1].Value + "\n" + m.Groups[2].Value + "filter:");
            if (newC != c) {
                File.WriteAllText(f, newC);
                Console.WriteLine("Fixed: " + Path.GetFileName(f));
            }
        }
        Console.WriteLine("Done");
    }
}
