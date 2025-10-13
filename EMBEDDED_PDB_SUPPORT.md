# Embedded PDB Support

## Overview

The tool now detects and preserves embedded PDB information from NuGet packages, ensuring that CompLog files include the necessary compiler flags to replicate assemblies with embedded symbols.

## What Was Fixed

When a NuGet package assembly has an embedded PDB (portable PDB embedded directly in the DLL), the compiler arguments stored in the PDB don't explicitly include the `/debug:embedded` flag. This is because:

1. The `/debug:embedded` flag affects the **PE file structure** (Debug Directory entries)
2. The compilation options stored in the PDB are **structured metadata**, not command-line arguments
3. These are two separate pieces of information that need to be combined

## How It Works

### Detection

The tool now inspects the PE Debug Directory to detect:
- `DebugDirectoryEntryType.EmbeddedPortablePdb` - Indicates `/debug:embedded`
- `DebugDirectoryEntryType.Reproducible` - Indicates `/deterministic+`

### Preservation

When these entries are detected, the tool:
1. Adds `/debug:embedded` to the compiler arguments file
2. Adds `/deterministic+` to the compiler arguments file
3. These flags are then included in the generated CompLog

### Reconstruction

When the CompLog is replayed with `complog export` and built:
- The compiler receives both the structured compilation options and the debug flags
- The resulting assembly has the same PE structure as the original
- The embedded PDB is properly included

## Example

```bash
# Download JustSaying 8.0.0 and create complog
dotnet run -- JustSaying 8.0.0

# Extract and build the complog
complog export JustSaying.8.0.0.complog
cd .complog/export/JustSaying
bash build-*.sh

# Verify the output has embedded PDB
# (Use any PE inspection tool)
# Result: output/JustSaying.dll has EmbeddedPortablePdb ✓
```

## Technical Details

### Why This Matters

Modern NuGet packages often use deterministic builds with embedded PDBs for:
- **Portability** - No need to distribute separate PDB files
- **Source Link** - Debugging experience without local sources
- **Reproducibility** - Deterministic builds produce identical binaries

Without these flags, a replayed compilation would produce:
- An assembly without embedded symbols
- Different binary content (fails byte-for-byte comparison)
- Missing deterministic build marker

### Implementation

The key changes are in `PdbCompilerArgumentsExtractor.cs`:

1. Read PE Debug Directory entries during PDB extraction
2. Pass `hasEmbeddedPdb` and `hasReproducibleMarker` flags through extraction methods
3. Add corresponding compiler flags to the arguments list
4. Store them in `compiler-arguments.txt` as actual command-line arguments

And in `CompLogFileCreator.cs`:

1. Parse compiler arguments file to handle both structured (key-value) and command-line format
2. Store command-line arguments in a special `__extra_args__` key
3. Include these extra arguments when building the compilation

## Verification

To verify an assembly has an embedded PDB:

```csharp
using var stream = File.OpenRead("assembly.dll");
using var peReader = new PEReader(stream);
var hasEmbedded = peReader.ReadDebugDirectory()
    .Any(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
```

Or use the `complog` tool's diagnostics.
