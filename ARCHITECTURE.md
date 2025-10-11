# NuGet to CompLog - Architecture

## Overview

This tool is designed to extract all information needed to create a CompLog (Compilation Log) file from a NuGet package. A CompLog is a portable, self-contained representation of a Roslyn compilation that includes source code, references, and compiler arguments.

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────┐
│                      Program.cs (Entry Point)                │
│  - Parse command-line arguments                              │
│  - Invoke CompilerArgumentsExtractor                         │
└─────────────────────┬───────────────────────────────────────┘
                      │
                      ▼
┌─────────────────────────────────────────────────────────────┐
│            CompilerArgumentsExtractor.cs                     │
│  ┌─────────────────────────────────────────────────────┐   │
│  │ 1. Download Package (.nupkg)                         │   │
│  │    - Connect to nuget.org via NuGet.Protocol        │   │
│  │    - Resolve version (latest or specified)          │   │
│  │    - Download to temp directory                     │   │
│  └─────────────────────────────────────────────────────┘   │
│  ┌─────────────────────────────────────────────────────┐   │
│  │ 2. Download Symbols (.snupkg) [Optional]            │   │
│  │    - Attempt to download from nuget.org             │   │
│  │    - Many packages don't have symbols packages      │   │
│  └─────────────────────────────────────────────────────┘   │
│  ┌─────────────────────────────────────────────────────┐   │
│  │ 3. Extract Package Contents                         │   │
│  │    - Unzip .nupkg (it's a zip file)                 │   │
│  │    - Unzip .snupkg if available                     │   │
│  └─────────────────────────────────────────────────────┘   │
│  ┌─────────────────────────────────────────────────────┐   │
│  │ 4. Find Assemblies                                  │   │
│  │    - Search lib/ folder (implementation assemblies) │   │
│  │    - Search ref/ folder (reference assemblies)      │   │
│  │    - Handle multi-targeting (multiple TFMs)         │   │
│  └─────────────────────────────────────────────────────┘   │
│  ┌─────────────────────────────────────────────────────┐   │
│  │ 5. Extract Compiler Arguments (per assembly)       │   │
│  │    - Delegate to PdbCompilerArgumentsExtractor     │   │
│  └───────────────────┬─────────────────────────────────┘   │
└────────────────────┬─┴─────────────────────────────────────┘
                     │
                     ▼
┌─────────────────────────────────────────────────────────────┐
│         PdbCompilerArgumentsExtractor.cs                     │
│  ┌─────────────────────────────────────────────────────┐   │
│  │ 1. Locate PDB                                       │   │
│  │    a) Check for embedded PDB in assembly            │   │
│  │    b) Look for external PDB next to assembly        │   │
│  │    c) Search symbols package directory              │   │
│  └─────────────────────────────────────────────────────┘   │
│  ┌─────────────────────────────────────────────────────┐   │
│  │ 2. Read PDB using System.Reflection.Metadata        │   │
│  │    - MetadataReader for portable PDBs               │   │
│  │    - PEReader for embedded PDBs                     │   │
│  └─────────────────────────────────────────────────────┘   │
│  ┌─────────────────────────────────────────────────────┐   │
│  │ 3. Extract Custom Debug Information                 │   │
│  │    - Compilation Options (compiler args)            │   │
│  │    - Metadata References (referenced assemblies)    │   │
│  │    - Document Table (source files)                  │   │
│  │    - Source Link (git mappings)                     │   │
│  │    - Embedded Source (when available)               │   │
│  └─────────────────────────────────────────────────────┘   │
│  ┌─────────────────────────────────────────────────────┐   │
│  │ 4. Display Extracted Information                    │   │
│  │    - Compiler command-line arguments                │   │
│  │    - List of referenced assemblies                  │   │
│  │    - Source file paths                              │   │
│  │    - Source Link configuration                      │   │
│  └─────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

## Data Flow

### Input
- Package ID (e.g., "Newtonsoft.Json")
- Version (optional, defaults to latest stable)

### Processing Steps

1. **Package Download**
   - Input: Package ID + Version
   - Output: .nupkg file path + .snupkg file path (if available)
   - Technology: NuGet.Protocol API

2. **Package Extraction**
   - Input: .nupkg and .snupkg paths
   - Output: Extracted directory structure
   - Technology: System.IO.Compression.ZipFile

3. **Assembly Discovery**
   - Input: Extracted package directory
   - Output: List of assembly paths (organized by target framework)
   - Logic: Search lib/ and ref/ folders recursively

4. **PDB Location**
   - Input: Assembly path
   - Output: PDB file or embedded PDB data
   - Strategy:
     - Check PE header for embedded PDB
     - Check CodeView debug directory for external PDB path
     - Search extracted directories

5. **Metadata Extraction**
   - Input: PDB (embedded or file)
   - Output: Structured compiler information
   - Technology: System.Reflection.Metadata APIs
   - Custom Debug Information GUIDs:
     - `B5FEEC05-8CD0-4A83-96DA-466284BB4BD8` - Compilation Options
     - `7E4D4708-096E-4C5C-AEDA-CB10BA6A740D` - Metadata References
     - `CC110556-A091-4D38-9FEC-25AB9A351A6A` - Source Link
     - `0E8A571B-6926-466E-B4AD-8AB04611F5FE` - Embedded Source

### Output
Currently: Console output showing all extracted information
Future: CompLog file package

## Key Components

### CompilerArgumentsExtractor
**Responsibilities:**
- Orchestrate the entire extraction process
- Manage NuGet package downloads
- Handle file system operations
- Coordinate PDB extraction for multiple assemblies

**Dependencies:**
- NuGet.Protocol (package management)
- System.IO.Compression (zip extraction)

### PdbCompilerArgumentsExtractor
**Responsibilities:**
- Locate PDB files (embedded or external)
- Read portable PDB format
- Parse custom debug information
- Extract and display compilation metadata

**Dependencies:**
- System.Reflection.Metadata (PDB reading)
- System.Reflection.PortableExecutable (PE file reading)

## PDB Format Details

### Portable PDB Structure
```
Portable PDB
├── Module Metadata
├── Document Table (source files)
│   ├── File paths
│   ├── Language GUIDs
│   └── Hash algorithms
├── Method Debug Information
│   ├── Sequence points
│   └── Local scopes
└── Custom Debug Information
    ├── Compilation Options
    │   └── Command-line arguments (null-terminated strings)
    ├── Compilation Metadata References
    │   ├── Reference count
    │   └── For each reference:
    │       ├── File name
    │       ├── Extern aliases
    │       └── Properties
    ├── Source Link
    │   └── JSON mapping (local paths → repository URLs)
    └── Embedded Source
        └── Compressed source code per document
```

### Compilation Options Format
- Stored as blob of null-terminated UTF-8 strings
- Contains all compiler command-line arguments:
  - `/debug+`, `/optimize+`, `/deterministic+`
  - `/langversion:preview`
  - `/define:TRACE;RELEASE`
  - Reference paths
  - etc.

### Metadata References Format
Binary format:
```
[Int32] Count
For each reference:
  [Int32] FileName Length
  [Bytes] FileName (UTF-8)
  [Int32] Extern Alias Count
  For each alias:
    [Int32] Alias Length
    [Bytes] Alias (UTF-8)
  [Byte]  EmbedInteropTypes flag
  (Additional properties...)
```

## Future Architecture: CompLog Creation

### Planned Components (Not Yet Implemented)

```
┌──────────────────────────────────────────────────────────┐
│         DependencyResolver.cs (Future)                    │
│  - Parse .nuspec file                                     │
│  - Resolve dependency graph                               │
│  - Download all transitive dependencies                   │
│  - Handle version conflicts                               │
└──────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────┐
│         FrameworkAssemblyResolver.cs (Future)             │
│  - Identify target framework                              │
│  - Download reference assemblies                          │
│  - Handle framework version matching                      │
└──────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────┐
│         SourceCodeExtractor.cs (Future)                   │
│  - Extract embedded source from PDBs                      │
│  - Parse Source Link JSON                                 │
│  - Download source from git repositories                  │
│  - Preserve directory structure                           │
└──────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────┐
│         CompLogPackager.cs (Future)                       │
│  - Create CompLog directory structure                     │
│  - Package all artifacts                                  │
│  - Generate manifest/metadata                             │
│  - Compress to final format                               │
└──────────────────────────────────────────────────────────┘
```

## Error Handling Strategy

### Current Handling
- Package not found: Exception with clear message
- Version not found: Exception with clear message
- Symbols not available: Warning, continue processing
- PDB not found: Warning, cannot extract compiler info
- PDB parse error: Warning, display partial information

### Robustness Considerations
- Not all packages have reproducible builds
- Not all packages include symbols
- Some packages use Windows PDB format (not supported)
- Multi-targeting packages create multiple assemblies
- Source Link may reference private repositories

## Extensibility Points

### Custom Package Sources
Currently hardcoded to nuget.org, but could be extended to:
- Private NuGet feeds
- Local package cache
- Alternative package sources

### PDB Format Support
Currently only supports Portable PDB:
- Could add Windows PDB support via DIA SDK (platform-specific)
- Could support other debug formats

### Output Formats
Currently console output only:
- JSON output for programmatic use
- XML metadata files
- CompLog binary format

### Source Retrieval
Not yet implemented, but could support:
- Git repository cloning
- Source Link URL downloads
- Embedded source extraction
- Local source linking

## Performance Considerations

### Current Implementation
- Downloads are sequential (not parallelized)
- All assemblies processed (even if duplicate)
- Temp files not cleaned up (relies on OS temp cleanup)

### Optimization Opportunities
- Parallel assembly processing
- Caching of downloaded packages
- Incremental processing (skip already-processed items)
- Smart cleanup of temporary files
- Target framework selection (process only desired TFM)

## Security Considerations

### Current Security Posture
- Downloads from nuget.org (trusted source)
- No validation of package signatures
- Extracts to temp directory (isolated)
- No code execution from packages

### Future Security Enhancements
- Validate package signatures
- Verify checksums
- Sandbox extraction operations
- Limit resource consumption
- Validate Source Link URLs before downloading

## Testing Strategy

### Manual Testing
- Test with various package types
- Test with/without symbols
- Test multi-targeting packages
- Test deterministic vs non-deterministic builds

### Recommended Test Packages
- **With Symbols**: Modern Microsoft.* packages
- **Multi-targeting**: Newtonsoft.Json
- **Deterministic**: Most recent .NET libraries
- **No Symbols**: Many older packages

### Future Automated Testing
- Unit tests for PDB parsing
- Integration tests for package download
- Mock NuGet feeds for reliable testing
- Regression tests for various package formats
