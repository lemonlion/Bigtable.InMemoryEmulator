using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

var assembly = typeof(Google.Cloud.Bigtable.V2.ExecuteQueryRequest).Assembly;
Console.WriteLine($"Assembly: {assembly.FullName}\n");

// =====================================================
// 1. Bigtable.BigtableBase - ExecuteQuery method signature
// =====================================================
Console.WriteLine("=" .PadRight(60, '='));
Console.WriteLine("1. Bigtable.BigtableBase - ExecuteQuery methods");
Console.WriteLine("=" .PadRight(60, '='));
var bigtableBase = assembly.GetType("Google.Cloud.Bigtable.V2.Bigtable+BigtableBase");
if (bigtableBase != null)
{
    var methods = bigtableBase.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(m => m.Name.Contains("ExecuteQuery"))
        .ToList();
    foreach (var m in methods)
    {
        var paramStr = string.Join(", ", m.GetParameters().Select(p => $"{FormatTypeName(p.ParameterType)} {p.Name}"));
        Console.WriteLine($"  {FormatTypeName(m.ReturnType)} {m.Name}({paramStr})");
    }
    if (methods.Count == 0) Console.WriteLine("  (no methods found)");
}

// Also check BigtableClient (the gRPC generated client)
Console.WriteLine("\nBigtable.BigtableClient - ExecuteQuery methods:");
var bigtableClient = assembly.GetType("Google.Cloud.Bigtable.V2.Bigtable+BigtableClient");
if (bigtableClient != null)
{
    var methods = bigtableClient.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(m => m.Name.Contains("ExecuteQuery"))
        .ToList();
    foreach (var m in methods)
    {
        var paramStr = string.Join(", ", m.GetParameters().Select(p => $"{FormatTypeName(p.ParameterType)} {p.Name}"));
        Console.WriteLine($"  {FormatTypeName(m.ReturnType)} {m.Name}({paramStr})");
    }
    if (methods.Count == 0) Console.WriteLine("  (no methods found)");
}

// =====================================================
// 2. ExecuteQueryRequest - all fields
// =====================================================
Console.WriteLine("\n" + "=".PadRight(60, '='));
Console.WriteLine("2. ExecuteQueryRequest");
Console.WriteLine("=".PadRight(60, '='));
InspectFull(typeof(Google.Cloud.Bigtable.V2.ExecuteQueryRequest));

// =====================================================
// 3. ExecuteQueryResponse - all fields
// =====================================================
Console.WriteLine("\n" + "=".PadRight(60, '='));
Console.WriteLine("3. ExecuteQueryResponse");
Console.WriteLine("=".PadRight(60, '='));
InspectFull(typeof(Google.Cloud.Bigtable.V2.ExecuteQueryResponse));

// =====================================================
// 4. ResultSetMetadata, ProtoSchema, ColumnMetadata
// =====================================================
Console.WriteLine("\n" + "=".PadRight(60, '='));
Console.WriteLine("4. ResultSetMetadata");
Console.WriteLine("=".PadRight(60, '='));
Inspect("Google.Cloud.Bigtable.V2.ResultSetMetadata");

Console.WriteLine("\n--- ProtoSchema ---");
Inspect("Google.Cloud.Bigtable.V2.ProtoSchema");

Console.WriteLine("\n--- ColumnMetadata ---");
Inspect("Google.Cloud.Bigtable.V2.ColumnMetadata");

// =====================================================
// 5. PartialResultSet, ProtoRowsBatch
// =====================================================
Console.WriteLine("\n" + "=".PadRight(60, '='));
Console.WriteLine("5. PartialResultSet & ProtoRowsBatch");
Console.WriteLine("=".PadRight(60, '='));
Inspect("Google.Cloud.Bigtable.V2.PartialResultSet");
Console.WriteLine("\n--- ProtoRowsBatch ---");
Inspect("Google.Cloud.Bigtable.V2.ProtoRowsBatch");

// =====================================================
// 6. ProtoRows
// =====================================================
Console.WriteLine("\n" + "=".PadRight(60, '='));
Console.WriteLine("6. ProtoRows");
Console.WriteLine("=".PadRight(60, '='));
Inspect("Google.Cloud.Bigtable.V2.ProtoRows");

// Also look for ProtoFormat
Console.WriteLine("\n--- ProtoFormat ---");
Inspect("Google.Cloud.Bigtable.V2.ProtoFormat");

// =====================================================
// 7. Full Type hierarchy
// =====================================================
Console.WriteLine("\n" + "=".PadRight(60, '='));
Console.WriteLine("7. Type hierarchy (Google.Cloud.Bigtable.V2.Type)");
Console.WriteLine("=".PadRight(60, '='));
var typeType = typeof(Google.Cloud.Bigtable.V2.Type);
InspectFull(typeType);

// All nested types under Type.Types
Console.WriteLine("\n--- All Type.Types nested types (recursive) ---");
var typesContainer = assembly.GetType("Google.Cloud.Bigtable.V2.Type+Types");
if (typesContainer != null)
{
    InspectAllNested(typesContainer, 0, 4);
}

// =====================================================
// 8. Value kinds
// =====================================================
Console.WriteLine("\n" + "=".PadRight(60, '='));
Console.WriteLine("8. Value (Google.Cloud.Bigtable.V2.Value)");
Console.WriteLine("=".PadRight(60, '='));
InspectFull(typeof(Google.Cloud.Bigtable.V2.Value));

// Also ArrayValue
Console.WriteLine("\n--- ArrayValue ---");
Inspect("Google.Cloud.Bigtable.V2.ArrayValue");

// =====================================================
// Bonus: All *Query* types in the namespace
// =====================================================
Console.WriteLine("\n" + "=".PadRight(60, '='));
Console.WriteLine("Bonus: All Proto* and *Query* and *ResultSet* types");
Console.WriteLine("=".PadRight(60, '='));
foreach (var t in assembly.GetTypes()
    .Where(t => t.IsPublic && t.Namespace == "Google.Cloud.Bigtable.V2" 
        && (t.Name.Contains("Proto") || t.Name.Contains("Query") || t.Name.Contains("ResultSet") || t.Name.Contains("PartialResult")))
    .OrderBy(t => t.Name))
{
    Console.WriteLine($"  {t.Name}");
}

// =====================================================
// Bonus: BigtableServiceApiClient ExecuteQuery
// =====================================================
Console.WriteLine("\n" + "=".PadRight(60, '='));
Console.WriteLine("Bonus: BigtableServiceApiClient.ExecuteQuery");
Console.WriteLine("=".PadRight(60, '='));
var svcClient = assembly.GetType("Google.Cloud.Bigtable.V2.BigtableServiceApiClient");
if (svcClient != null)
{
    var methods = svcClient.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(m => m.Name.Contains("ExecuteQuery"))
        .ToList();
    foreach (var m in methods)
    {
        var paramStr = string.Join(", ", m.GetParameters().Select(p => $"{FormatTypeName(p.ParameterType)} {p.Name}"));
        Console.WriteLine($"  {FormatTypeName(m.ReturnType)} {m.Name}({paramStr})");
    }
}

// =====================================================
// Bonus: Grpc.Core IServerStreamWriter check
// =====================================================
Console.WriteLine("\n" + "=".PadRight(60, '='));
Console.WriteLine("Bonus: ExecuteQuery server call context type");
Console.WriteLine("=".PadRight(60, '='));
if (bigtableBase != null)
{
    var eqMethod = bigtableBase.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .FirstOrDefault(m => m.Name == "ExecuteQuery");
    if (eqMethod != null)
    {
        Console.WriteLine($"  Return type: {FormatTypeName(eqMethod.ReturnType)}");
        foreach (var p in eqMethod.GetParameters())
        {
            Console.WriteLine($"  Param: {FormatTypeName(p.ParameterType)} {p.Name}");
            // If it's a generic type, show the generic args
            if (p.ParameterType.IsGenericType)
            {
                foreach (var ga in p.ParameterType.GetGenericArguments())
                    Console.WriteLine($"    Generic arg: {FormatTypeName(ga)}");
            }
        }
    }
}

// =====================================================
// Helpers
// =====================================================

void Inspect(string typeName)
{
    var t = assembly.GetType(typeName);
    if (t == null) { Console.WriteLine($"  TYPE NOT FOUND: {typeName}"); return; }
    InspectFull(t);
}

void InspectFull(Type type)
{
    Console.WriteLine($"  Type: {type.FullName}");
    if (type.BaseType != null && type.BaseType != typeof(object) && type.BaseType != typeof(Enum) && type.BaseType != typeof(ValueType))
        Console.WriteLine($"  Base: {FormatTypeName(type.BaseType)}");

    if (type.IsEnum)
    {
        foreach (var val in Enum.GetValues(type))
            Console.WriteLine($"    {val} = {(int)val}");
        return;
    }

    var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .OrderBy(p => p.Name).ToArray();
    if (props.Length > 0)
    {
        Console.WriteLine($"  Properties ({props.Length}):");
        foreach (var p in props)
            Console.WriteLine($"    {FormatTypeName(p.PropertyType)} {p.Name}");
    }

    // Show oneof enums
    var nestedEnums = type.GetNestedTypes(BindingFlags.Public).Where(t => t.IsEnum).ToArray();
    foreach (var ne in nestedEnums)
    {
        Console.WriteLine($"  Enum {ne.Name}:");
        foreach (var val in Enum.GetValues(ne))
            Console.WriteLine($"    {val} = {(int)val}");
    }

    // Show nested non-enum types (names only)
    var nested = type.GetNestedTypes(BindingFlags.Public)
        .Where(t => !t.IsEnum && !t.Name.StartsWith("<"))
        .OrderBy(t => t.Name).ToArray();
    if (nested.Length > 0)
    {
        Console.WriteLine($"  Nested types:");
        foreach (var nt in nested)
            Console.WriteLine($"    {nt.Name}");
    }
}

void InspectAllNested(Type container, int depth, int maxDepth)
{
    if (depth >= maxDepth) return;
    var indent = new string(' ', depth * 2);
    
    var nested = container.GetNestedTypes(BindingFlags.Public)
        .Where(t => !t.Name.StartsWith("<"))
        .OrderBy(t => t.Name);
    
    foreach (var nt in nested)
    {
        if (nt.IsEnum)
        {
            Console.WriteLine($"{indent}ENUM {nt.Name}:");
            foreach (var val in Enum.GetValues(nt))
                Console.WriteLine($"{indent}  {val} = {(int)val}");
            continue;
        }

        Console.WriteLine($"{indent}CLASS {nt.Name}");
        var props = nt.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(p => p.Name);
        foreach (var p in props)
            Console.WriteLine($"{indent}  {FormatTypeName(p.PropertyType)} {p.Name}");
        
        // Show nested enums inline
        foreach (var ne in nt.GetNestedTypes(BindingFlags.Public).Where(t => t.IsEnum))
        {
            Console.WriteLine($"{indent}  ENUM {ne.Name}:");
            foreach (var val in Enum.GetValues(ne))
                Console.WriteLine($"{indent}    {val} = {(int)val}");
        }

        // Recurse into nested types
        var subNested = nt.GetNestedTypes(BindingFlags.Public).Where(t => !t.IsEnum && !t.Name.StartsWith("<")).ToArray();
        if (subNested.Length > 0)
        {
            InspectAllNested(nt, depth + 1, maxDepth);
        }
    }
}

string FormatTypeName(Type t)
{
    if (t == typeof(string)) return "string";
    if (t == typeof(int)) return "int";
    if (t == typeof(long)) return "long";
    if (t == typeof(bool)) return "bool";
    if (t == typeof(double)) return "double";
    if (t == typeof(float)) return "float";
    if (t == typeof(void)) return "void";
    if (t == typeof(byte[])) return "byte[]";

    if (t.IsGenericType)
    {
        var name = t.Name.Split('`')[0];
        var args = string.Join(", ", t.GetGenericArguments().Select(FormatTypeName));
        var ns = t.Namespace ?? "";
        if (ns.StartsWith("Google") || ns.StartsWith("Grpc"))
            return $"{name}<{args}>";
        return $"{name}<{args}>";
    }

    // For nested types, show the short "Outer.Inner" form
    if (t.DeclaringType != null)
    {
        return $"{t.DeclaringType.Name}.{t.Name}";
    }

    return t.Name;
}
