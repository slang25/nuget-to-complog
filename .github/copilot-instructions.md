# GitHub Copilot Instructions for NuGet to CompLog

## Project Overview

This is a .NET CLI tool that downloads NuGet packages and extracts CompLog (Compilation Log) files from them. A CompLog is a portable, self-contained compilation snapshot that includes all compiler arguments, references, and source files needed to recreate a Roslyn compilation workspace without any external dependencies except the C# compiler.

## Core Workflow

1. **Download NuGet Package**: User runs `dotnet run -- <PackageName> <Version>`
2. **Extract Package**: Tool extracts .nupkg and searches for PDB files
3. **Parse PDB**: Extracts compiler arguments, metadata references, and source info from portable PDBs
4. **Create CompLog**: Packages everything into a self-contained .complog file
5. **Usage**: External tools can then use `complog extract myproject.complog` and `complog replay myproject.complog`

## Key Commands

### Running the Tool
```bash
# Basic usage - downloads package and creates complog
dotnet run -- JustSaying 8.0.0

# Latest version
dotnet run -- Newtonsoft.Json

# Output is a .complog file in the current directory
```

### Working with CompLog Files
```bash
# Extract contents of a complog file
complog extract myproject.complog

# Replay/rebuild the compilation
complog replay myproject.complog
```

## Architecture

### Project Structure
```
src/NuGetToCompLog/
├── Program.cs                          # CLI entry point
├── CompilerArgumentsExtractor.cs       # NuGet download & orchestration
├── PdbCompilerArgumentsExtractor.cs    # PDB parsing logic
└── NuGetToCompLog.csproj              # Project file
```

### Key Components

1. **NuGet Download** (`CompilerArgumentsExtractor.cs`)
   - Uses NuGet.Protocol to download packages from nuget.org
   - Handles both .nupkg (package) and .snupkg (symbols) files
   - Extracts to temporary directories for processing

2. **PDB Extraction** (`PdbCompilerArgumentsExtractor.cs`)
   - Searches for PDBs in: embedded in assemblies, external files, symbol packages
   - Reads portable PDB format using System.Reflection.Metadata
   - Extracts custom debug information GUIDs for compiler data

3. **CompLog Creation**
   - Packages compiler arguments, all references, and source files
   - Creates self-contained .complog file
   - Ensures portability (no external dependencies needed)

## Important Technical Details

### PDB Custom Debug Information GUIDs
The tool extracts specific metadata from portable PDBs:
- `B5FEEC05-8CD0-4A83-96DA-466284BB4BD8` - Compilation options (compiler args)
- `7E4D4708-096E-4C5C-AEDA-CB10BA6A740D` - Metadata references (assemblies)
- `CC110556-A091-4D38-9FEC-25AB9A351A6A` - Source Link configuration
- `0E8A571B-6926-466E-B4AD-8AB04611F5FE` - Embedded source files

### Package Requirements
For successful CompLog extraction, packages should have:
- ✅ Deterministic builds enabled
- ✅ Portable PDB files (not Windows PDB format)
- ✅ Embedded or available symbols
- ✅ Source Link configured (for source file access)

### Target Frameworks
The tool handles multi-targeting packages by processing each TFM (Target Framework Moniker) separately, such as net8.0, netstandard2.0, net462, etc.

## Dependencies

- **NuGet.Protocol** - Official NuGet client library for package downloads
- **System.Reflection.Metadata** - Microsoft's portable PDB reader
- **System.IO.Compression** - For .nupkg/.snupkg extraction (ZIP format)
- **Spectre.Console** - Rich console UI with colors, tables, and progress indicators

## Code Style & Conventions

### When Writing Code
- Use async/await throughout for I/O operations
- Provide comprehensive error handling with helpful diagnostics
- Add inline comments explaining complex logic, especially around PDB parsing
- Use Spectre.Console for rich, informative output
- Keep separation of concerns: download logic separate from extraction logic

### Naming Conventions
- Async methods end with `Async`
- Use descriptive variable names (e.g., `packageIdentity` not `pkg`)
- Constants for debug info GUIDs
- Clear separation between NuGet and PDB concepts

### Error Handling
- Gracefully handle missing symbols (very common)
- Continue processing even if some assemblies fail
- Provide clear diagnostic messages about what's missing and why
- Warn about limitations rather than failing silently

## Common Development Tasks

### Adding Support for New PDB Information
1. Identify the custom debug info GUID from Roslyn source
2. Add constant to `PdbCompilerArgumentsExtractor.cs`
3. Add parsing logic in the appropriate method
4. Update output display to show new information

### Extending Package Sources
1. Modify `CompilerArgumentsExtractor.cs` to accept custom sources
2. Use NuGet.Protocol's SourceRepository with custom URI
3. Handle authentication if needed for private feeds

### Improving Dependency Resolution
Currently the tool processes single packages. To add dependency resolution:
1. Parse .nuspec files from extracted packages
2. Use NuGet.Packaging libraries for dependency graph
3. Recursively download dependencies
4. Handle version conflicts and framework-specific dependencies

### Testing Changes
```bash
# Build the project
dotnet build

# Test with well-known packages
dotnet run -- Newtonsoft.Json 13.0.3
dotnet run -- System.Text.Json 8.0.0
dotnet run -- Microsoft.Extensions.Logging

# Verify complog output
complog extract output.complog
complog replay output.complog
```

## Documentation Files

- **README.md** - Project overview and usage
- **ARCHITECTURE.md** - Deep technical details (~12K words)
- **EXAMPLES.md** - Practical usage scenarios
- **QUICKSTART.md** - 3-minute getting started guide
- **TESTING.md** - Testing with custom packages
- **PROJECT_SUMMARY.md** - High-level project summary

## Important Gotchas

1. **Most packages don't include PDBs** - This is expected and normal. The tool handles this gracefully.
2. **Windows PDB format not supported** - Only portable PDBs work.
3. **Source Link is not always configured** - Even with PDBs, source files may not be accessible.
4. **Multi-targeting complexity** - Packages with multiple TFMs need per-TFM processing.
5. **Framework references** - Need to be resolved separately (e.g., Microsoft.NETCore.App.Ref).

## Future Enhancements (from TODOs)

When implementing new features, refer to extensive TODO comments in the code:
- Dependency resolution and transitive package downloads
- Framework assembly resolution and download
- Source code extraction from Source Link or embedded sources
- CompLog file format definition and packaging
- Validation of complete compilations

## Working with CompLog Files

The ultimate output of this tool is a .complog file that can be consumed by the `complog` CLI tool:

```bash
# After running this tool to create myproject.complog

# Extract to examine contents
complog extract myproject.complog
# This unpacks all sources, references, and compiler arguments

# Replay the compilation
complog replay myproject.complog
# This invokes csc.exe with all the original arguments and files
# Should produce an identical assembly to the original
```

## Performance Considerations

- Uses temporary directories for isolation (cleaned up automatically)
- Stream-based downloads to minimize memory
- Efficient ZIP extraction (only relevant files)
- Minimal memory footprint for PDB reading (metadata reader API)

## Security Considerations

- Only downloads from nuget.org (or specified sources)
- No code execution during extraction
- Temporary files are isolated per run
- Source code may contain sensitive information (handle appropriately)

## Help with Debugging

### Common Issues
1. **"Symbols package not found"** - Normal, most packages don't publish .snupkg
2. **"No PDB found"** - Package wasn't built with embedded PDBs
3. **"Failed to extract compiler arguments"** - PDB format issue or missing custom debug info
4. **Version resolution failures** - Package version doesn't exist or network issues

### Diagnostic Commands
```bash
# Enable verbose output (if implemented)
dotnet run -- --verbose PackageName Version

# Check package availability
# (Visit nuget.org/packages/PackageName)
```

## When Assisting Users

1. **For usage questions**: Refer to QUICKSTART.md or README.md
2. **For technical details**: Point to ARCHITECTURE.md
3. **For compilation issues**: CompLog must have all references and sources
4. **For package issues**: Explain PDB requirements and deterministic builds
5. **For feature requests**: Check TODO comments in code for planned work

## Expected User Workflows

### Basic Workflow
```bash
# User wants to extract a complog from a package
cd nuget-to-complog
dotnet run -- JustSaying 8.0.0

# Output: JustSaying.8.0.0.complog in current directory

# User can now replay the compilation
complog replay JustSaying.8.0.0.complog
```

### Advanced Workflow
```bash
# Extract the complog to inspect
complog extract JustSaying.8.0.0.complog

# Make changes to source files or compiler arguments
# (in extracted directory)

# Rebuild with modified settings
csc @compiler-args.txt
```

## Code Quality Standards

- ✅ No compiler errors
- ✅ Minimal warnings (document any intentional ones)
- ✅ Comprehensive inline comments for complex logic
- ✅ XML documentation for public APIs
- ✅ Async/await for all I/O operations
- ✅ Proper disposal of IDisposable resources
- ✅ Meaningful variable and method names
- ✅ SOLID principles where applicable

## Remember

This tool bridges the gap between NuGet packages and Roslyn compilation workspaces. The CompLog format is the key output - it must contain **everything** needed to recreate the compilation with zero external dependencies (except csc.exe itself). Think of it as a "compilation container" similar to a Docker container for builds.
