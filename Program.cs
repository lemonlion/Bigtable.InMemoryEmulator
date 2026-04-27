using System.Reflection;
var clientType = typeof(Google.Cloud.Bigtable.V2.BigtableClient);
var methods = clientType.GetMethods().Where(m => m.Name == "ReadRows");
foreach (var m in methods) {
    var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
    Console.WriteLine($"{m.Name}({ps})");
}