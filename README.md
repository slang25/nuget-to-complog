# NuGet to CompLog

A tool to extract compilation information from NuGet packages.

![NuGet to CompLog in action](./docs/usage.gif)

## What does it do?

Imagine you find an interesting NuGet package on nuget.org and want to understand exactly how it was compiled. This tool takes a package name, downloads it, and extracts all the compiler settings, references, and source information that was baked into it. The result is a **CompLog** file—a portable, self-contained snapshot containing everything needed to rebuild that package from source.

**In plain English:** It's like taking a snapshot of a build. Everything that went into compiling that package gets captured in one `.complog` file that you can move around and use to replay the original compilation.

## Install

```bash
dotnet tool install -g NuGetToCompLog
```

This installs the `nuget-to-complog` command globally. Upgrade with `dotnet tool update -g NuGetToCompLog`.

## Example

```bash
# Download Newtonsoft.Json and extract its compilation info
nuget-to-complog Newtonsoft.Json 13.0.3

# Creates: Newtonsoft.Json.13.0.3.complog
# This file now contains all compiler settings, dependencies, and sources
```

## Why would I use this?

- **Understand how packages are built** - See exact compiler flags, optimizations, and settings
- **Verify reproducibility** - Confirm you can rebuild a package identically
- **Analyze dependencies** - Inspect what each package references
- **Archive build information** - Keep a permanent snapshot of how a package was compiled
- **Security auditing** - Examine source and compilation details of dependencies

## Quick Start

```bash
# Install
dotnet tool install -g NuGetToCompLog

# Extract a package's compilation info
nuget-to-complog Newtonsoft.Json 13.0.3
```

This creates a `.complog` file in your current directory with all the compilation details.

### Verifying reproducibility

```bash
# Prove a package round-trips: rebuild it from the complog and byte-compare
nuget-to-complog verify Serilog 4.4.0
```

The `verify` command creates a complog, exports it, rebuilds with the exact compiler version
recorded in the PDB (when installed locally), and byte-compares the result against the assembly
shipped in the package. Exit codes: `0` byte-for-byte match, `2` content matches but derived
fields (MVID, timestamps, signature, PDB id) drift, `1` real differences.

To make rebuilds faithful, the tool:

- lists sources in the exact PDB Documents order (source order affects assembly bytes)
- verifies every source against the PDB checksum and repairs line-ending/BOM drift
  (Source Link serves committed bytes; the original build may have used a CRLF checkout)
- resolves reference assemblies by MVID, locating the exact targeting pack version on nuget.org
- carries Source Link and embedded sources (`/sourcelink`, `/embed`) into the complog
- reconstructs strong naming: `/publicsign` from the assembly's public key, or full signing
  when the repo commits its `.snk` (RSA signing is deterministic)

### Swapping a dependency for its source

```bash
# Replace a project's PackageReference with the package built from its recovered source
nuget-to-complog swap Serilog --project src/MyApp
```

The `swap` command finds the `PackageReference` in the consuming project (resolving the version
from the project file or `Directory.Packages.props`), ejects the package's original source into
`patches/<PackageId>+<Version>/`, generates an SDK-style `.csproj` over it, and rewrites the
`PackageReference` into a `ProjectReference`. From then on plain `dotnet build` compiles the
dependency from source — edit files under `patches/<PackageId>+<Version>/src/` and build as
usual. Other packages that depend on the swapped package pick up the source-built project too,
via NuGet's project-over-package rule.

Undo the swap by reverting the consuming project file (`git checkout`); capture your edits as a
committable patch with `nuget-to-complog diff <PackageId>`. See
[docs/guides/PATCH_PACKAGE.md](./docs/guides/PATCH_PACKAGE.md) for the full patching workflow.

### Teaching your coding agent (agent skills)

The tool bundles [Agent Skills](https://agentskills.io) that teach coding agents (Claude Code,
Codex CLI, Gemini CLI, and others) to reach for these workflows on their own — instead of
decompiling, cloning the upstream repo, or copying DLLs around. There are two, because they have
opposite intents and an agent has to choose between them up front:

| | |
|---|---|
| `swap-nuget-dependency` | change what a dependency does — debug it, patch it, instrument it |
| `source-build-nuget-package` | keep it identical, but compile it yourself, for licensing or provenance |

```bash
# Print a skill to stdout
nuget-to-complog skill
nuget-to-complog skill --name source-build-nuget-package

# Install them for Claude Code (user-level, ~/.claude/skills/)
nuget-to-complog skill --install

# Or project-level / other agents
nuget-to-complog skill --install --project
nuget-to-complog skill --install --agent codex   # claude | codex | gemini | agents
```

Installed skills are stamped with the tool version; after `dotnet tool update -g nugettocomplog`,
re-run `nuget-to-complog skill --install` to refresh them. The canonical copies live under
[skills/](./skills/), so `npx skills add slang25/nuget-to-complog` and
`gh skill install slang25/nuget-to-complog` work too.


### Consuming a package as a source build

Two lines in the project file, and nothing to install:

```xml
<PackageReference Include="NuGetToCompLog.SourceBuild" Version="0.4.0" PrivateAssets="all" />
<PackageReference Include="Serilog" Version="4.0.0" SourceBuild="true" />
```

`dotnet build` now compiles against an assembly built on this machine from Serilog's own source.
The first build reconstructs and caches it; later builds use the cache. There is no lock file, no
generated targets and no separate step: the marker is the whole configuration, and everything else
the substitution needs — the version the graph resolved, the lib folder this target framework
picked, the assembly name — the build already knows and passes through.

For a package reached only transitively, there is no `PackageReference` to mark, so name it
directly:

```xml
<SourceBuildPackage Include="Serilog" />
```

`nuget-to-complog sourcebuild <PackageId>` will write the marker for you if you would rather not
hand-edit, but it is only editing the project file.

This is `swap`'s opposite number: it ejects nothing. The package is reconstructed, compiled with
the exact compiler its PDB records where that is installed, checked against the binary the package
shipped, and cached; the source stays sealed inside the complog next to the cached assembly.
Restore is untouched, so the dependency graph — transitive packages included — resolves exactly as
before, and `deps.json` still describes the package as it always did. Only the assembly the
compiler reads and the output directory receives is different.

That combination is what the [Open Source Maintenance Fee](https://opensourcemaintenancefee.org)
model asks for: the source is freely licensed, the published binary is the thing being paid for,
and compiling it yourself is the alternative the agreement names. A package's OSMF-licensed
transitive dependencies keep resolving as published packages, since nothing about the graph
changes.

**It stays private to your repository.** The build package ships its assets under `build/`, not
`buildTransitive/`, and is a `DevelopmentDependency`. If you pack a library that source-builds one
of its dependencies, nothing about that reaches the people who consume your package — their build
resolves the published binary as usual, and neither the substitution nor the build package appears
in your nuspec. Replacing a dependency's binary is a decision about your own build; it is not one
you get to make for anybody downstream.

To build against the published binaries again without unpicking anything:

```bash
dotnet build -p:NuGetToCompLogDisableSourceBuild=true
```

#### Building a package from source is not a quiet operation

The first build after a clone downloads the package, fetches its sources over the network, and
runs its source generators in a child process. That happens once, only for a marked package whose
assembly is not already cached, and it announces itself in the build log rather than happening
silently. `-p:NuGetToCompLogAutoBuild=false` turns the automatic path off and makes a missing
assembly an error instead.

Point `NUGET_TO_COMPLOG_CACHE` at a directory your CI caches, next to where it caches
`~/.nuget/packages`, and the cost is paid once rather than per run. Parallel builds of a solution
that all reach a cold cache are safe: the cache is locked per entry, and whoever waits picks up the
assembly the first one built.

Properties that shape the build: `NuGetToCompLogFetchCompiler` downloads the exact compiler the
package used, `NuGetToCompLogSkipGenerators` keeps package-authored generator code out of the
process, and `NuGetToCompLogAllowDivergent` accepts a rebuild that is not the same library.

#### How it decides the rebuild is the same library

You do not need the compiler the package was built with — your SDK's compiler is fine, which is
what building a library from its repository yourself would use anyway. But that changes what
counts as evidence, so the check changes with it:

**When the exact compiler and runtime ran** (they are installed, or `--fetch-compiler` fetched
them), the bytes are authoritative and are compared directly:

| | |
|---|---|
| `Identical` | byte-for-byte |
| `ContentEquivalent` | every content byte matches; only fields derived from the signing key and PDB differ |

For a signed package `Identical` is unreachable — the Authenticode signature covers bytes only the
publisher's key can produce — so `ContentEquivalent` is the realistic pass, not a near miss. If the
bytes still differ beyond that, the toolchain was right and the inputs were not, so it is refused.

**Otherwise the bytes mean nothing.** Two Roslyn versions compiling identical source differ as a
matter of course, so "the bytes differ" cannot tell a harmless codegen change from a rebuild that
dropped a type. What survives the toolchain is what a consumer can actually observe, so that is
what gets compared:

| | |
|---|---|
| `ApiEquivalent` | every public type, member and signature matches the shipped assembly, and so does every referenced assembly identity |

That second half matters as much as the first. A rebuild whose own surface is untouched but which
binds to `Microsoft.Extensions.Options 10.0.0.0` where the original bound to `6.0.0.0` is not the
same library, and is refused. Differing MVIDs at the *same* identity are fine — that is one
dependency rebuilt, not a different dependency.

It is not a proof of identical behaviour; two assemblies can share a surface and differ inside. It
is the strongest compiler-independent evidence available, and it is checked alongside the
reconstruction ledger, which separately accounts for every input that went into the compilation.

#### What it refuses to do

- **Source recovered from the shipped assembly.** If the ledger had to decompile a document,
  the result is derived from the binary rather than compiled from source, and it is not the
  independently compiled binary the licence carves out. Inferred compiler *flags* are fine —
  they change how the compilation is configured, not where its code came from.
- **A rebuild that is not the same library**, by whichever standard applies above.
  `--allow-divergent` accepts it deliberately; the provenance record then says so.
- **A marker that resolves nothing.** A package named for source building that contributes no
  assembly — a typo, or an analyzer-only package — fails the build with `NTCL1001`. So does a
  cached assembly that no longer matches what was recorded for it (`NTCL1002`). Quietly building
  against the published binary when a source build was asked for is the one outcome this feature
  cannot have.

`provenance.json`, written beside the cached assembly, records which standard was met and on what:

```json
{
  "packageId": "Serilog",
  "packageVersion": "4.0.0",
  "equivalence": "ApiEquivalent",
  "compiler": "4.14.0-3.25465.8",
  "compilerWasExact": false,
  "runtimeWasExact": false,
  "surfaceMembersChecked": 580,
  "reconstructionOutlook": "unconfirmed"
}
```

### Packages that ship more than one assembly

A working directory describes one compilation, so a package that ships several assemblies for the
same target framework (NUnit's `nunit.framework.dll` next to `nunit.framework.legacy.dll`) is
captured one at a time. The default is the assembly named after the package; `--assembly` picks
another:

```bash
# capture the sibling instead of the assembly named after the package
nuget-to-complog verify <package> <version> --assembly nunit.framework.legacy.dll
```

### Running source generators

A generated document can only be reproduced by the generator that produced it: csc embeds the
generated text itself, hashed with the generator's own checksum algorithm, so passing the same
characters as a plain file yields a different PDB and a different assembly. So the tool finds the
generator assembly the original build used and runs it — here, in this process — to prove it
regenerates those documents byte-for-byte before attaching it as `/analyzer`.

That means package-controlled code executes on your machine. It is the same exposure as building
a project that references the generator, but you only asked to read a package, so there is a
switch: `--skip-generators` (or `NUGET_TO_COMPLOG_SKIP_GENERATORS=1`) keeps generator code out of
the process. The generated documents then go in as plain source files, which the ledger records as
a substitution and which cannot reproduce the original PDB exactly.

### What the package didn't record

A NuGet package is not a self-describing build. The PDB records the compiler version, the
options blob, the references and most sources — but nothing records the analyzer set, the
source generator package versions (they are `PrivateAssets`, so the nuspec never sees them),
or flags like `/features:` and `/nowarn:`. So this tool does not *extract* a complog from a
package; it **reconstructs** one, filling those gaps by inference.

Every run writes a `{package}.{version}.reconstruction.json` next to the complog saying where
each input came from, and prints a summary:

```
Reconstruction ledger
     13  recorded     read from the package and verified
      2  derived      computed from recorded data
      1  inferred     recovered from evidence in the shipped assembly
    153  proven       searched for, then confirmed against the package
      1  substituted  knowingly not the original input
    104  missing      needed and not recovered
  ⚠ Some inputs are knowingly not the original - the rebuild will compile but cannot match:
    • source ByteString.cs: neither embedded in the PDB nor available from Source Link
    • signing /publicsign: the assembly is fully signed but no matching .snk was found ...
```

The distinctions are the point:

| | meaning |
|---|---|
| `recorded` | read from the package and verified — source bytes that hash to the PDB's checksum |
| `derived` | computed from recorded data, like the `/pathmap` root |
| `inferred` | never stated, but implied by the shipped assembly (`/features:nullablePublicOnly`) |
| `proven` | searched for among candidates, then confirmed — a reference matched by MVID |
| `assumed` | a guess nothing could confirm. Might be right; not claimed |
| `substituted` | knowingly not the original — decompiled source, a stand-in assembly |
| `missing` | needed by the compilation and not recovered |

The last three are what stop a rebuild being a faithful replay, and the ledger's verdict says
which: `exact` (everything accounted for), `unconfirmed` (only guesses in the way), or
`impossible` (something is knowingly not the original). A complog whose ledger says `exact`
should rebuild byte-for-byte; one that says `impossible` will compile but cannot match, and
now says so before you run the build rather than after.

The file carries no timestamps or machine paths and its entries are emitted in a fixed order,
so the same package always produces the same ledger — commit one and a diff shows exactly how
reconstruction quality changed.

### Building from source

```bash
git clone https://github.com/slang25/nuget-to-complog.git
cd nuget-to-complog
dotnet build
dotnet run --project src/NuGetToCompLog -- Newtonsoft.Json 13.0.3
```

## How it works

1. **Downloads the package** from nuget.org
2. **Finds the PDB files** (debug symbols with compiler information)
3. **Extracts compiler settings** like optimization flags, target framework, and references
4. **Packages everything** into a portable `.complog` file

## Important note

Not all packages include the necessary information. For a CompLog to be created successfully, the package needs to have been built with:
- Deterministic builds enabled
- Portable PDB files (not Windows PDB format)
- Embedded or available symbols

Most modern packages meet these requirements, but older packages or packages not built with SDK-style projects may not. The tool handles this gracefully—if information can't be found, it will tell you why, and the reconstruction ledger records every gap it had to fill.

## What you get

The `.complog` file contains:
- Exact compiler command-line arguments
- All referenced assemblies and their versions
- Source file paths and content
- Metadata about the build

You can then use the `complog` CLI tool to extract or replay the compilation.

## Documentation

See the [docs](./docs) folder for detailed documentation:

- [Architecture](docs/ARCHITECTURE.md) - Deep technical details about how the tool works
- [Project Summary](docs/PROJECT_SUMMARY.md) - Project overview
- [Changelog](docs/CHANGELOG.md) - What's changed
- [Quick Start Guide](docs/guides/QUICKSTART.md) - Get started in 3 minutes
- [Examples](docs/guides/EXAMPLES.md) - Usage examples
