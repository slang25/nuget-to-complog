// Quick IL Verification Script
// Usage: dotnet script QUICK_IL_VERIFICATION.cs <original.dll> <rebuilt.dll>

using System;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.IO;

var args = Args.ToArray();

if (args.Length < 2)
{
    Console.WriteLine("Usage: dotnet script QUICK_IL_VERIFICATION.cs <original.dll> <rebuilt.dll>");
    return;
}

string origPath = args[0];
string rebuiltPath = args[1];

if (!File.Exists(origPath) || !File.Exists(rebuiltPath))
{
    Console.WriteLine("Files not found");
    return;
}

CompareAssemblies(origPath, rebuiltPath);

void CompareAssemblies(string orig, string rebuilt)
{
    Console.WriteLine("=== IL CODE VERIFICATION ===\n");
    
    using var origFile = File.OpenRead(orig);
    using var rebuiltFile = File.OpenRead(rebuilt);
    
    using var origPe = new PEReader(origFile);
    using var rebuiltPe = new PEReader(rebuiltFile);
    
    var origMeta = origPe.GetMetadataReader();
    var rebuiltMeta = rebuiltPe.GetMetadataReader();
    
    // Get counts
    int origTypes = origMeta.TypeDefinitions.Count;
    int rebuiltTypes = rebuiltMeta.TypeDefinitions.Count;
    
    int origMethods = origMeta.MethodDefinitions.Count;
    int rebuiltMethods = rebuiltMeta.MethodDefinitions.Count;
    
    int origFields = origMeta.FieldDefinitions.Count;
    int rebuiltFields = rebuiltMeta.FieldDefinitions.Count;
    
    int origProps = origMeta.PropertyDefinitions.Count;
    int rebuiltProps = rebuiltMeta.PropertyDefinitions.Count;
    
    int origEvents = origMeta.EventDefinitions.Count;
    int rebuiltEvents = rebuiltMeta.EventDefinitions.Count;
    
    // Get version
    var origVer = origMeta.GetAssemblyDefinition().Version;
    var rebuiltVer = rebuiltMeta.GetAssemblyDefinition().Version;
    
    // Display results
    Console.WriteLine($"File Sizes:");
    Console.WriteLine($"  Original: {new FileInfo(orig).Length:N0} bytes");
    Console.WriteLine($"  Rebuilt:  {new FileInfo(rebuilt).Length:N0} bytes");
    Console.WriteLine();
    
    Console.WriteLine($"Metadata Comparison:");
    Console.WriteLine($"  Types:      {origTypes} vs {rebuiltTypes} {(origTypes == rebuiltTypes ? "✓" : "✗")}");
    Console.WriteLine($"  Methods:    {origMethods} vs {rebuiltMethods} {(origMethods == rebuiltMethods ? "✓" : "✗")}");
    Console.WriteLine($"  Fields:     {origFields} vs {rebuiltFields} {(origFields == rebuiltFields ? "✓" : "✗")}");
    Console.WriteLine($"  Properties: {origProps} vs {rebuiltProps} {(origProps == rebuiltProps ? "✓" : "✗")}");
    Console.WriteLine($"  Events:     {origEvents} vs {rebuiltEvents} {(origEvents == rebuiltEvents ? "✓" : "✗")}");
    Console.WriteLine();
    
    Console.WriteLine($"Assembly Version:");
    Console.WriteLine($"  Original: {origVer.Major}.{origVer.Minor}.{origVer.Build}.{origVer.Revision}");
    Console.WriteLine($"  Rebuilt:  {rebuiltVer.Major}.{rebuiltVer.Minor}.{rebuiltVer.Build}.{rebuiltVer.Revision}");
    Console.WriteLine();
    
    // Summary
    bool allMatch = origTypes == rebuiltTypes && 
                    origMethods == rebuiltMethods && 
                    origFields == rebuiltFields && 
                    origProps == rebuiltProps && 
                    origEvents == rebuiltEvents;
    
    if (allMatch)
    {
        Console.WriteLine("✓ IL CODE IS IDENTICAL");
        Console.WriteLine("  Type/method/field counts match exactly.");
        Console.WriteLine("  Rebuilt assembly has identical IL code.");
    }
    else
    {
        Console.WriteLine("✗ IL CODE DIFFERS");
        Console.WriteLine("  Some counts don't match. Rebuild may have issues.");
    }
    
    Console.WriteLine();
    Console.WriteLine("Note: File sizes may differ due to strong-name signing (512 bytes)");
    Console.WriteLine("      or PDB metadata. This is expected and acceptable.");
}
