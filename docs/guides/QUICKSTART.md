# Quick Start Guide

Get started with NuGet to CompLog in 3 minutes.

## Prerequisites

- .NET SDK 6.0 or later installed
- Internet connection (to download from nuget.org)

## Install & Run

```bash
# Clone and build
git clone <repository-url>
cd nuget-to-complog/NuGetToCompLog
dotnet build

# Run with a package
dotnet run -- Newtonsoft.Json 13.0.3
```

## What Happens

1. **Downloads** the NuGet package from nuget.org
2. **Extracts** assemblies from the package
3. **Searches** for PDB files (symbols)
4. **Extracts** compiler arguments and metadata
5. **Displays** all discovered information

## Expected Output

```
Processing package: Newtonsoft.Json 13.0.3
✓ Downloaded package
✓ Extracted package
✓ Found 8 assemblies
⚠ Symbols package not found (common)
✗ No PDB found (expected for most packages)
```

## Try These Packages

```bash
# Popular packages
dotnet run -- Newtonsoft.Json
dotnet run -- System.Text.Json 8.0.0
dotnet run -- Microsoft.Extensions.Logging

# See which have symbols available
```

## Understanding Results

- **✓ Found embedded PDB** = Success! Full extraction possible
- **⚠ Symbols package not found** = Normal, most packages don't publish symbols
- **✗ No PDB found** = Expected, package needs deterministic builds enabled

## Next Steps

- Read [README.md](README.md) for detailed usage
- Check [EXAMPLES.md](EXAMPLES.md) for more scenarios
- See [ARCHITECTURE.md](ARCHITECTURE.md) for technical details

## Need Help?

Run the help command:
```bash
dotnet run -- --help
```

---

**Note:** Most packages on nuget.org don't include PDBs, which is expected. The tool will tell you what's available and what's needed for full CompLog extraction.
