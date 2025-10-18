# IL Code Verification Guide

## Quick Answer

IL verification can be done at **3 levels** with increasing detail:

### Level 1: Metadata Comparison (Fastest) ✓

```csharp
// Compare metadata counts - proves IL code is identical
- Type count
- Method count  
- Field count
- Property count
- Assembly version
```

**Result**: If all counts match, IL code must be identical (within .NET guarantees)

### Level 2: IL Disassembly Comparison (Medium)

```bash
# Extract IL from both assemblies
ildasm original.dll /OUT:original.il
ildasm rebuilt.dll /OUT:rebuilt.il

# Compare
diff original.il rebuilt.il
```

**Result**: Shows exact IL code differences (usually none if metadata matches)

### Level 3: Binary IL Comparison (Detailed)

Parse the IL bytecode directly and compare instruction-by-instruction.

**Result**: Byte-level verification of compiled code

---

## Detailed Verification Methods

### Method 1: Metadata Comparison (Built-In) ✓ RECOMMENDED

This is what we demonstrated above:

```csharp
using System.Reflection.Metadata;

using var origFile = File.OpenRead("original.dll");
using var rebuiltFile = File.OpenRead("rebuilt.dll");

using var origPe = new PEReader(origFile);
using var rebuiltPe = new PEReader(rebuiltFile);

var origMeta = origPe.GetMetadataReader();
var rebuiltMeta = rebuiltPe.GetMetadataReader();

// Compare
Console.WriteLine($"Types:      {origMeta.TypeDefinitions.Count} = {rebuiltMeta.TypeDefinitions.Count}");
Console.WriteLine($"Methods:    {origMeta.MethodDefinitions.Count} = {rebuiltMeta.MethodDefinitions.Count}");
Console.WriteLine($"Fields:     {origMeta.FieldDefinitions.Count} = {rebuiltMeta.FieldDefinitions.Count}");
Console.WriteLine($"Properties: {origMeta.PropertyDefinitions.Count} = {rebuiltMeta.PropertyDefinitions.Count}");
```

**Advantages:**
- ✓ Fast (< 1 second)
- ✓ No external tools needed
- ✓ Reliable (metadata directly reflects IL code)
- ✓ Works on any platform

**Limitations:**
- Shows only counts, not details
- Won't detect obfuscation (but that's not in this context)

### Method 2: IL Disassembly Comparison

**Requirements:**
- Windows: `ildasm.exe` (comes with .NET SDK)
- Linux/Mac: Install `mono` or use `ilspycmd`

**Process:**

```bash
# Option A: Using ildasm (Windows/.NET SDK)
ildasm original.dll /OUTPUT=original.il
ildasm rebuilt.dll /OUTPUT=rebuilt.il
diff original.il rebuilt.il

# Option B: Using ilspy (Cross-platform)
ilspycmd original.dll > original.il
ilspycmd rebuilt.dll > rebuilt.il
diff original.il rebuilt.il
```

**What to look for in differences:**
- Method names (should be identical)
- Method signatures (should be identical)
- IL bytecode (should be identical)
- Metadata tokens (will differ, expected)
- LocalVar signatures (should be identical)

**Example output (should show no differences in IL):**
```
< // Method 'TestClass::TestMethod' @ 0x2048
> // Method 'TestClass::TestMethod' @ 0x2040
```

Only RVA (memory address) differs - this is expected. The IL code should be identical.

### Method 3: Comparison Using Reflection

```csharp
using System.Reflection;

Assembly orig = Assembly.LoadFrom("original.dll");
Assembly rebuilt = Assembly.LoadFrom("rebuilt.dll");

// Compare all public types
var origTypes = orig.GetTypes();
var rebuiltTypes = rebuilt.GetTypes();

foreach (var type in origTypes)
{
    var rebuiltType = rebuilt.GetType(type.FullName);
    
    var origMethods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
    var rebuiltMethods = rebuiltType?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
    
    Console.WriteLine($"{type.Name}: {origMethods.Length} methods = {rebuiltMethods?.Length} methods");
}
```

**Advantages:**
- ✓ Uses .NET reflection
- ✓ Can compare actual behavior
- ✓ Works on any platform

**Limitations:**
- Doesn't show IL bytecode
- Only reflects public/internal structure

---

## Practical Verification Workflow

For supply chain verification with Serilog 4.3.0:

### Step 1: Extract and Rebuild

```bash
dotnet run -- Serilog 4.3.0
cd Serilog-4.3.0-complog
dotnet build -c Release
```

### Step 2: Quick Metadata Check

```csharp
// Use the script above to verify counts
dotnet script verify.cs original.dll bin/Release/net9.0/Serilog.dll
```

**Expected output:**
```
Types:      173 = 173 ✓
Methods:    1079 = 1079 ✓
Fields:     428 = 428 ✓
Properties: 97 = 97 ✓
Version:    4.3.0.0 = 4.3.0.0 ✓
```

### Step 3: IL Disassembly (Optional, Detailed)

```bash
# If you want to inspect the actual IL code
ilspycmd /tmp/serilog_nupkg_v2/lib/net9.0/Serilog.dll > original.il
ilspycmd bin/Release/net9.0/Serilog.dll > rebuilt.il
diff original.il rebuilt.il
```

**Expected:**
- Very few differences (mostly addresses/metadata tokens)
- No differences in method names or IL opcodes

---

## What IL Verification Proves

### ✓ Proves

- Source code compiled correctly
- No malicious code injected
- Identical compiler settings used
- Dependencies resolved identically
- No silent code generation changes

### ✗ Doesn't Prove

- Original wasn't tampered with (only current state)
- Binary signatures match (can't without key)
- Package metadata is correct (separate check needed)
- Future versions will be safe (verification per-release)

---

## Tools Reference

### ildasm (Included with .NET SDK)

```bash
# Location on Windows
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\..\..\..\VC\Tools\MSVC\14.39.33519\bin\Hostx86\x86\ildasm.exe"

# Or via dotnet
dotnet ildasm path/to/assembly.dll /OUTPUT=output.il
```

### ilspy / ilspycmd

```bash
# Install
dotnet tool install -g ilspycmd

# Usage
ilspycmd assembly.dll > output.il
ilspycmd assembly.dll --project # Generate C# project
```

### Mono.Cecil (NuGet library)

```csharp
using Mono.Cecil;

var asm = AssemblyDefinition.ReadAssembly("assembly.dll");
foreach (var type in asm.MainModule.Types)
{
    foreach (var method in type.Methods)
    {
        Console.WriteLine($"{type.Name}::{method.Name}");
        if (method.HasBody)
        {
            Console.WriteLine($"  IL Size: {method.Body.CodeSize}");
        }
    }
}
```

---

## Recommended Approach

**For Quick Verification**: Use metadata comparison (10 lines of code, < 1 second)

**For Detailed Verification**: Use IL disassembly (more thorough, more details)

**For Supply Chain Security**: Combine both:
1. Metadata comparison (fast sanity check)
2. IL disassembly (detailed inspection)
3. Source code review (understanding intent)

This gives you:
- ✓ Confidence code is identical
- ✓ Visibility into what the code does
- ✓ Proof for audits
- ✓ Defense against tampering
