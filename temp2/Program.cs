using System;
using System.Linq;
using System.Reflection;
using Google.Cloud.Bigtable.V2;
var bt = typeof(Bigtable.BigtableBase);
foreach (var m in bt.GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly).Where(m=>!m.IsSpecialName).OrderBy(m=>m.Name))
{
    var ps = string.Join(", ", m.GetParameters().Select(p=>p.ParameterType.Name+" "+p.Name));
    Console.WriteLine($"{m.ReturnType.Name} {m.Name}({ps})");
}
