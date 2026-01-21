# Agent Instructions

Instructions for AI coding agents working on this codebase.

## Project Overview

A .NET CLI tool that downloads NuGet packages and extracts CompLog (Compilation Log) files from them. A CompLog is a portable, self-contained compilation snapshot that includes all compiler arguments, references, and source files needed to recreate a Roslyn compilation workspace.

## Project Structure

```
src/NuGetToCompLog/
├── Program.cs                          # CLI entry point
├── Domain/                             # Immutable value objects
├── Abstractions/                       # Service interfaces
├── Services/                           # Business logic
│   ├── NuGet/                          # Package downloading
│   ├── Pdb/                            # PDB parsing
│   └── CompLog/                        # CompLog creation
├── Infrastructure/                     # External concerns
├── Commands/                           # Command handlers
└── Exceptions/                         # Custom exceptions

tests/NuGetToCompLog.Tests/             # Unit and integration tests
docs/                                   # All documentation
```

## Running the Tool

```bash
# Build
dotnet build

# Basic usage - downloads package and creates complog
dotnet run -- Newtonsoft.Json 13.0.3

# Latest version
dotnet run -- Microsoft.Extensions.Logging
```

## Running Tests

```bash
dotnet test
```

## Key Technical Details

### PDB Custom Debug Information GUIDs

The tool extracts metadata from portable PDBs using these GUIDs:

- `B5FEEC05-8CD0-4A83-96DA-466284BB4BD8` - Compilation options (compiler args)
- `7E4D4708-096E-4C5C-AEDA-CB10BA6A740D` - Metadata references (assemblies)
- `CC110556-A091-4D38-9FEC-25AB9A351A6A` - Source Link configuration
- `0E8A571B-6926-466E-B4AD-8AB04611F5FE` - Embedded source files

### Package Requirements

For successful CompLog extraction, packages need:

- Deterministic builds enabled
- Portable PDB files (not Windows PDB format)
- Embedded or available symbols
- Source Link configured (for source file access)

## Code Conventions

- Use async/await for I/O operations
- Async methods end with `Async`
- Use descriptive variable names
- Add comments for complex PDB parsing logic
- Use Spectre.Console for console output
- Keep separation between download and extraction logic

## Dependencies

- **NuGet.Protocol** - Package downloads from nuget.org
- **System.Reflection.Metadata** - Portable PDB reading
- **System.IO.Compression** - .nupkg/.snupkg extraction
- **Spectre.Console** - Rich console UI
- **ConsoleAppFramework** - CLI argument parsing
- **Microsoft.Extensions.DependencyInjection** - DI container

## Common Gotchas

1. Most packages don't include PDBs - this is expected
2. Windows PDB format not supported - only portable PDBs
3. Source Link is not always configured
4. Multi-targeting packages need per-TFM processing
5. Framework references need separate resolution

## Documentation

All documentation lives in `docs/`:

- `docs/README.md` - Documentation index
- `docs/guides/` - Usage guides
- `docs/investigation/` - Technical analysis
