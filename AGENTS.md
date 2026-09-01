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

# Verify byte-for-byte reproducibility (rebuild from complog + compare)
dotnet run -- verify Serilog 4.4.0

# Swap a consuming project's PackageReference for the package built from source
dotnet run -- swap Serilog --project path/to/App

# Mark a project's PackageReference as a source build (equivalent to hand-editing the csproj)
dotnet run -- sourcebuild Serilog --project path/to/App

# Build and cache specific assets - how the MSBuild targets invoke the tool
dotnet run -- sourcebuild --assets "Serilog/4.0.0/lib/net8.0/Serilog.dll"
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

### The Reconstruction Ledger

A package does not record everything csc was given (no analyzer set, no generator package
versions, no `/features:` or `/nowarn:`), so the tool infers those and records what it did in
`Services/Reconstruction/ReconstructionLedger.cs`. Any code that decides where an input comes
from should say so on the ledger: `Recorded`/`Derived`/`Inferred`/`Proven` for inputs we can
stand behind, `Assumed`/`Substituted`/`Missing` for the ones that stop a rebuild being a
faithful replay. Prefer classifying by *evidence* (does it hash to the recorded checksum? does
the MVID match?) over the code path that produced it. Roll uniform groups into one entry with a
`Count`; name problems individually. The written file is a golden file — no timestamps, no
machine paths, stable ordering.

### Source Builds

`sourcebuild` makes a project consume a package as an assembly compiled here rather than the one
the package shipped, without ejecting source anywhere. Marking a PackageReference
`SourceBuild="true"` is the entire configuration.

- **`src/NuGetToCompLog.SourceBuild`** is a build-only NuGet package holding the MSBuild logic, so
  fixing it is a version bump rather than a re-run in every repository. It also carries the tool
  under `tools/net10.0/` so nothing has to be installed; the targets run it in a **child process**,
  never as a task, because it binds Roslyn, NuGet.Protocol and a decompiler and MSBuild already has
  its own copies of the first two loaded.
- **The build is the source of truth for what to build.** `ResolvePackageAssets` has already
  decided the version the graph resolved and the exact asset this target framework picked, so the
  task reads `NuGetPackageId`/`NuGetPackageVersion`/`PathInPackage` off the resolved items and
  passes them to the tool as `--assets <id>/<version>/<pathInPackage>`. Do not reintroduce a lock
  file or a project-file probe: both are worse guesses at something the build already knows, and
  both can disagree with it.
- **`SourceBuildCache`** keys on `(package id, version, lib TFM)` and stores the assembly, the
  complog it came from and `<assembly>.provenance.json` together. That layout is the only contract
  between the tool and the task — `CachedAssemblyContractTests` is what stops the two drifting
  apart. The record is per *assembly*, not per entry: one package and TFM can ship several
  (`nunit.framework.dll` beside `nunit.framework.legacy.dll`), a build resolves all of them, and
  caching the second must not evict the first.

Rules that are easy to break:

1. **Rewrite `HintPath` with the item spec.** `ResolveAssemblyReferences` prefers a reference's
   `HintPath` over its identity, so an item carrying the package folder's `HintPath` resolves
   straight back to the published binary — the substitution then appears to work everywhere except
   the one place that decides what the compiler reads.
2. **Never fall back silently.** A marker that resolves no assembly, a cached file that fails its
   recorded hash, and a managed asset no source build covers (a RID-specific assembly under
   `runtimes/`) are hard errors (`NTCL1001`, `NTCL1002`, `NTCL1003`). Only native and satellite
   assets are passed over quietly, because neither carries the library's own code. Building
   against the published binary when a source build was asked for is the failure this feature
   exists to prevent, and it has no other symptom.
3. **The substitution must never escape the repository that chose it.** Assets ship under `build/`
   and never `buildTransitive/`, and the package is a `DevelopmentDependency`. Replacing a
   dependency's binary is a decision about your own build, not one to make for anyone who consumes
   your package.
4. **The props and targets are shipped as-is; nothing else validates them.** An XML comment
   containing `--` is illegal and produces a package that cannot be imported at all, and `pack`
   will not notice. `SourceBuildBuildAssetTests` loads both files as XML for exactly this reason.
5. **The task takes no dependencies.** It is loaded into a host with its own assembly graph, so it
   reads the one field it needs out of `provenance.json` by hand. Adding a package reference here
   risks binding against the wrong copy of something at build time.

`SourceBuildCommandHandler.Classify` picks the standard the rebuild is held to, and the choice is
the subtle part. A byte comparison is only evidence when the exact compiler *and* runtime ran
(`RebuildOutcome.ToolchainWasExact`); under any other toolchain, differing bytes are the normal
result of compiling the same source with a different Roslyn and say nothing. There the standard is
`AssemblySurfaceComparer`: every public type, member and signature, plus every referenced assembly
identity (name/version/token, deliberately not the MVID — a dependency rebuilt at the same identity
is not a different dependency). Do not "improve" this by comparing bytes in both regimes; it would
refuse every source build on a machine that lacks a compiler the dnceng feed has aged out.

`SourceBuildCommandHandler.PassesSourceProvenanceGate` refuses to proceed when the ledger holds a
`Substituted` entry in the `source` category: source recovered from the shipped assembly is derived
from the binary, not compiled from source. Substitutions in other categories are fine — a
public-signing stand-in or an inferred flag changes how the compilation is configured, not where
its code came from.

### Agent Skills

`skills/` holds the skills the tool ships; `nuget-to-complog skill --install` writes them into an
agent's skills directory, and they are embedded in the assembly so an installed copy always matches
the tool version. Adding one is a directory, an `EmbeddedResource` line whose `LogicalName` is
`<name>/SKILL.md`, and an entry in `SkillCommandHandler.SkillNames`.

There are deliberately two, split on **intent** rather than mechanism: `swap-nuget-dependency`
changes what a dependency does, `source-build-nuget-package` keeps it identical and compiles it
locally. They share almost all their machinery, so the temptation is to merge them - don't. Their
success criteria are inverted (a source build *fails* when the rebuild differs from the shipped
assembly; a patch *requires* it to differ), and an agent chooses between them from the descriptions
alone, before it starts. Each description must therefore name the other and say when not to use
itself; `BundledSkillsTests` enforces that. Changing a description means re-running the eval suites
under `skills/*/evals/`, since three-way trigger disambiguation is what regresses first.

### Reference Acquisition

A PDB's metadata references carry file names and MVIDs but **no versions**, and a nuspec states a
range rather than what restore resolved. So when a reference has to be fetched from nuget.org, the
version comes from the shipped assembly's `AssemblyRef` table
(`AssemblySurfaceComparer.ReadReferencedAssemblyVersions`) — the only precise record of which build
of a dependency took part in the compilation.

Two places must keep using it, and both were previously wrong in the same way:

- `AcquireNuGetReferencesAsync` used to fall straight to `GetLatestPackageVersionAsync`. For any
  package that has been out a while, the newest release is the wrong answer — AutoMapper 13.0.1
  built against `Microsoft.Extensions.Options 6.0.0` and got 10.0.x.
- `TryFindExactPackageAssemblyAsync` probes versions by MVID, newest-first, with a bounded window.
  When the answer is an old release there are far more than `maxProbes` newer versions in front of
  it, so the probe never reached it. Versions sharing the recorded assembly's major/minor now go
  first.

Package version and assembly version are not the same thing in general, so both paths treat the
recorded version as a *guess* and let the MVID check reject it. Do not turn either into an
assertion.

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
