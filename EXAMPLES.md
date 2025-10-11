# Quick Start Examples

## Installation

```bash
git clone <repository-url>
cd nuget-to-complog/NuGetToCompLog
dotnet build
```

## Basic Usage

### Example 1: Analyze Latest Version of a Package

```bash
dotnet run -- Newtonsoft.Json
```

**Output:**
```
Processing package: Newtonsoft.Json (latest)
Working directory: /tmp/nuget-to-complog/...

Latest version: 13.0.3
✓ Downloaded package to: /tmp/.../Newtonsoft.Json.13.0.3.nupkg
✓ Extracted package to: /tmp/.../extracted
✓ Found 8 assemblies:
  - lib/netstandard1.0/Newtonsoft.Json.dll
  - lib/net35/Newtonsoft.Json.dll
  - lib/netstandard2.0/Newtonsoft.Json.dll
  - lib/net6.0/Newtonsoft.Json.dll
  ...

Attempting to download symbols package (.snupkg)...
⚠ Symbols package (.snupkg) not found
...
```

### Example 2: Analyze Specific Version

```bash
dotnet run -- System.Text.Json 8.0.0
```

### Example 3: Analyze a Microsoft Package

```bash
dotnet run -- Microsoft.Extensions.Logging
```

## Understanding the Output

### Package Download Phase
- Shows which package and version is being processed
- Displays download location
- Lists all assemblies found in the package

### Symbols Discovery
- Attempts to download .snupkg (symbols package)
- If found, extracts and lists PDB files
- If not found, shows a warning

### Per-Assembly Analysis
For each assembly, the tool attempts to:

1. **Locate PDB**
   - `✓ Found embedded PDB` - Best case, PDB is in the assembly
   - `→ PDB reference: <path>` - Assembly references external PDB
   - `✗ No PDB found` - Cannot extract compiler info

2. **Extract Compilation Options** (if PDB found)
   ```
   Compiler Arguments:
     /debug+
     /optimize+
     /deterministic+
     /langversion:preview
     /define:TRACE;RELEASE
   ```

3. **Extract Metadata References** (if PDB found)
   ```
   Metadata References:
     Total references: 42
     [1] System.Runtime.dll
     [2] System.Collections.dll
     ...
   ```

4. **Extract Source Files** (if PDB found)
   ```
   SOURCE FILES:
     [1] /src/MyProject/Class1.cs
     [2] /src/MyProject/Class2.cs
     Total: 156 source files
   ```

5. **Source Link Configuration** (if available)
   ```
   SOURCE LINK CONFIGURATION:
   {
     "documents": {
       "C:\\src\\*": "https://raw.githubusercontent.com/user/repo/commit/*"
     }
   }
   ```

## Common Scenarios

### ✅ Successful Extraction
**Package has embedded PDB with deterministic builds:**
```bash
dotnet run -- SomePackage 1.0.0
# Shows full compiler arguments, references, and source files
```

### ⚠️ No Symbols Available
**Package doesn't include PDB files:**
```bash
dotnet run -- OldPackage 1.0.0
# Shows: "✗ No PDB found - cannot extract compiler arguments"
# Still shows package structure and assemblies
```

### 📦 Multi-Targeting Package
**Package targets multiple frameworks:**
```bash
dotnet run -- Newtonsoft.Json
# Processes each TFM separately:
#   - netstandard1.0
#   - netstandard2.0
#   - net6.0
#   - etc.
```

## What to Look For

### Good Candidates for CompLog Creation
Packages with:
- ✅ Embedded PDBs or symbols packages
- ✅ Deterministic builds enabled
- ✅ Source Link configured
- ✅ Modern SDK-style projects

### Poor Candidates
Packages with:
- ❌ No PDB files available
- ❌ Non-deterministic builds
- ❌ Windows PDB format only
- ❌ Very old packages

## Next Steps After Analysis

Once you've confirmed a package has extractable compiler information:

1. **Review compiler arguments** - Understand build configuration
2. **Examine metadata references** - Plan dependency resolution
3. **Check Source Link** - Determine if source is accessible
4. **Plan CompLog structure** - Design packaging format

See `ARCHITECTURE.md` for details on future CompLog packaging implementation.

## Troubleshooting

### "Package not found"
- Check package ID spelling (case-sensitive)
- Verify package exists on nuget.org
- Check internet connectivity

### "No PDB found"
- Package doesn't include symbols
- Try checking if .snupkg is available separately
- Contact package maintainer about adding symbols

### "Could not download symbols package"
- Most packages don't publish .snupkg files
- This is expected and not an error
- Look for embedded PDBs instead

### Build Warnings
The NU1510 warnings about "will not be pruned" are harmless:
- These packages are transitive dependencies
- They don't need to be explicitly referenced
- The warnings don't affect functionality

## Advanced Usage (Future)

Once CompLog packaging is implemented:

```bash
# Create CompLog from package
dotnet run -- create-complog Newtonsoft.Json 13.0.3 -o output/

# Extract specific target framework
dotnet run -- create-complog MyPackage --tfm net8.0

# Include all dependencies
dotnet run -- create-complog MyPackage --include-dependencies

# Download sources from Source Link
dotnet run -- create-complog MyPackage --download-sources
```

These features are planned but not yet implemented.
