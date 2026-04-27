using System.Reflection;
var baseType = typeof(Google.Cloud.Bigtable.V2.Bigtable.BigtableBase);
foreach (var m in baseType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
    Console.WriteLine(m.Name);
