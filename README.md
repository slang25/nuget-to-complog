# NuGet to CompLog

A .NET command-line tool that downloads NuGet packages and extracts compiler arguments from PDB files to prepare for creating CompLog files.

## What is a CompLog?

A CompLog (Compilation Log) is a portable format that contains everything needed to create a Roslyn workspace. It's essentially a serialized version of the `csc` command line arguments, but fully portable because it packages up:
- Reference assemblies (framework assemblies)
- NuGet package assemblies  
- Source code
- Compiler arguments and options

Think of it as a deterministic snapshot of a compilation that can be moved between machines, similar to an MSBuild binlog but without the cruft and fully portable.

## Current Features

This tool currently implements the foundational steps:

1. **Download NuGet packages** from nuget.org
2. **Extract package contents** to examine assemblies
3. **Attempt to download symbol packages** (.snupkg files)
4. **Extract compiler arguments from PDBs** when available
5. **Display compilation metadata** including:
   - Compiler command-line arguments
   - Metadata references
   - Source file listings
   - Source Link configuration

## Usage

```bash
dotnet run -- <package-id> [version]

# Examples:
dotnet run -- Newtonsoft.Json 13.0.3
dotnet run -- System.Text.Json 8.0.0
dotnet run -- Microsoft.Extensions.Logging
```

If no version is specified, the latest stable version is downloaded.

## How It Works

### 1. Package Download
Uses the NuGet.Protocol library to:
- Connect to nuget.org's V3 API
- Download the specified package (.nupkg)
- Attempt to download symbols package (.snupkg)

### 2. PDB Discovery
Searches for PDB files in three locations:
- **Embedded PDBs**: Modern packages often embed PDBs directly in assemblies
- **Symbols packages**: Separate .snupkg files containing PDB files
- **Package contents**: Some packages include PDBs in the main .nupkg

### 3. Compiler Argument Extraction
When a PDB is found (portable PDB format), extracts:
- **Compilation Options** (compiler command-line arguments)
- **Metadata References** (assemblies referenced during compilation)
- **Source Files** (file paths from the original build)
- **Source Link** (mapping to source control URLs)

This information is stored in custom debug information entries in the PDB:
- `B5FEEC05-8CD0-4A83-96DA-466284BB4BD8` - Compilation options
- `7E4D4708-096E-4C5C-AEDA-CB10BA6A740D` - Metadata references
- `CC110556-A091-4D38-9FEC-25AB9A351A6A` - Source Link
- `0E8A571B-6926-466E-B4AD-8AB04611F5FE` - Embedded source

## Limitations & Requirements

### Package Requirements
For this tool to extract complete information, packages must:
- ✅ Use **deterministic builds** (reproducible builds)
- ✅ Include **portable PDB files** (not Windows PDB format)
- ✅ Have **symbols available** (embedded or in .snupkg)

Many packages don't meet all these requirements, especially older packages or those not built with modern SDK-style projects.

### Current Limitations
- Only supports portable PDB format (not Windows PDB)
- Cannot access private symbol servers
- Does not yet construct actual CompLog files
- Multi-targeting packages process all TFMs separately

## Roadmap / TODO

The code includes extensive comments about what needs to be implemented for full CompLog creation:

### 1. Dependency Resolution
- Parse .nuspec files from extracted packages
- Recursively download all package dependencies
- Handle dependency version ranges
- Resolve dependencies per target framework

### 2. Framework Assembly Resolution  
- Identify target framework from compiler arguments
- Download reference assemblies (e.g., `Microsoft.NETCore.App.Ref`)
- Handle different framework versions (net8.0, netstandard2.0, etc.)
- Optionally use local SDK reference assemblies

### 3. Source Code Extraction
- Extract embedded source from PDBs
- Parse Source Link JSON mappings
- Download source files from git repositories
- Preserve directory structure for Roslyn workspace

### 4. CompLog Packaging
- Create standardized directory structure
- Package all references, sources, and compiler arguments
- Ensure deterministic/reproducible output
- Define compression format

### 5. Validation
- Verify all required references are available
- Check source file integrity
- Validate compiler arguments completeness
- Test Roslyn workspace recreation

## Building

```bash
cd NuGetToCompLog
dotnet build
dotnet run -- <package-id> [version]
```

## Dependencies

- **NuGet.Protocol** - For downloading packages from nuget.org
- **System.Reflection.Metadata** - For reading portable PDB files
- **System.IO.Compression** - For extracting .nupkg/.snupkg files

## Example Output

```
Processing package: System.Text.Json 8.0.0
Working directory: /tmp/nuget-to-complog/62e743b4-544d-4a21-8e5b-c46e467c4f48

✓ Downloaded package to: /tmp/.../System.Text.Json.8.0.0.nupkg
✓ Extracted package to: /tmp/.../extracted
✓ Found 5 assemblies:
  - lib/netstandard2.0/System.Text.Json.dll
  - lib/net6.0/System.Text.Json.dll
  - lib/net8.0/System.Text.Json.dll
  - lib/net462/System.Text.Json.dll
  - lib/net7.0/System.Text.Json.dll

⚠ Symbols package (.snupkg) not found

Processing assembly: System.Text.Json.dll
================================================================================
  ✓ Found embedded PDB
  
  COMPILATION OPTIONS:
  ----------------------------------------------------------------------------
  Compiler Arguments:
    /debug+
    /optimize+
    /deterministic+
    /langversion:preview
    ...
```

## Contributing

This is a foundational implementation. Key areas for contribution:
- Implementing dependency resolution
- Adding framework assembly download
- Source code extraction from Source Link
- CompLog file format definition and packaging
- Better error handling for missing symbols
- Support for Windows PDB format

## License

(Add your license here)
