---
name: source-build-nuget-package
description: "Make a .NET project build and ship a NuGet dependency from that package's own source instead of the binary the package published, without changing what the library does and without putting its source in the repository. Use this when the reason is licensing or provenance rather than behaviour: a package whose binary is licensed on different terms from its source, a policy that dependencies must be compiled in-house, wanting evidence that a published binary really matches its source, or simply not wanting to ship a vendor's compiled artifact. Trigger on \"build this package from source\", \"compile the dependency myself\", \"licensed binary\", \"don't ship the vendor's binary\", \"verify the binary matches the source\", \"build our dependencies in-house\". Do NOT use this to change, patch, instrument or debug a dependency — that is swap-nuget-dependency, which is the opposite intent and has the opposite success criteria."
metadata:
  version: dev
  checksum: dev
  source: nugettocomplog
---

# Consume a NuGet package as a source build

The build links against an assembly compiled on this machine from the package's own recovered
source, instead of the binary the package shipped. Nothing is ejected: the source stays sealed
inside a complog in a machine-local cache, and the repository gains no source files.

This is the opposite intent to `swap`. Swap makes a dependency *different*; a source build makes
it *the same, from a binary you compiled*. The tool enforces that difference — it refuses to cache
a rebuild that is not equivalent to the published assembly, which is exactly what patching needs to
do. If the goal is to change behaviour, stop and use `swap-nuget-dependency` instead.

## Why this exists

Some packages license the compiled binary on different terms from the source, and building the
library yourself is the stated alternative. Others sit under a policy that dependencies must be
compiled in-house. And sometimes the point is simply evidence: a source build that reproduces the
published assembly is proof the binary matches the source it claims to come from.

All three want the same thing — the library, unchanged, from a compiler you ran.

## Prerequisites

- .NET SDK 10+ (the build package carries the reconstruction tool; nothing else to install)
- The package must ship portable PDBs with SourceLink metadata

## Setup

Two lines in the consuming project:

```xml
<PackageReference Include="NuGetToCompLog.SourceBuild" Version="0.4.0" PrivateAssets="all" />
<PackageReference Include="Serilog" Version="4.0.0" SourceBuild="true" />
```

`dotnet build`. The first build reconstructs and caches the assembly; later builds use the cache.
There is no lock file, no generated file and no separate command — the `SourceBuild="true"` marker
is the whole configuration.

For a package reached only transitively there is no `PackageReference` to mark, so name it:

```xml
<SourceBuildPackage Include="Serilog" />
```

`nuget-to-complog sourcebuild <PackageId>` writes the marker for you, but it is only editing the
project file — hand-editing is equally fine.

## What to expect on the first build

It downloads the package, fetches its sources over the network, and runs its source generators in
a child process. This is announced in the build log and happens once per package. It is not a
mistake, and it does not repeat on later builds.

## How to read the result

The build refuses rather than silently falling back to the published binary. Three outcomes count
as success, in descending strength:

| | meaning |
|---|---|
| `Identical` | byte-for-byte with the shipped assembly |
| `ContentEquivalent` | every content byte matches; only signing- and PDB-derived fields differ |
| `ApiEquivalent` | built with a different compiler, so the bytes are not comparable — but every public type, member and signature matches, and so does every referenced assembly identity |

`ApiEquivalent` is the normal result when the exact compiler the package used is not installed, and
it is the same standard as building the library from its repository yourself. Set
`-p:NuGetToCompLogFetchCompiler=true` to download the original compiler and aim for the stronger
outcomes.

`<assembly>.provenance.json`, written beside the cached assembly it describes, records which was
reached. Read it when someone asks what a shipped binary actually is.

## When it refuses

- **Source recovered from the shipped assembly.** If the source couldn't be fetched and had to be
  decompiled, the result is derived from the binary rather than compiled from source, so it is not
  the independently compiled binary the licence carves out. There is no override; this one is the
  point of the feature.
- **A rebuild that is not the same library** — differing public surface, or binding to different
  dependency versions than the original. `-p:NuGetToCompLogAllowDivergent=true` accepts it
  deliberately.
- **A marker that resolves no assembly** (`NTCL1001`) — typically a typo, or an analyzer-only
  package that ships no lib assembly.
- **An asset a source build cannot cover** (`NTCL1003`) — a RID-specific managed assembly under
  `runtimes/`, say. Substituting the compile-time reference and leaving that one in place would
  compile against the source build and run against the vendor binary.

## Things to know

- **It stays private to your repository.** The build package ships `build/` assets, not
  `buildTransitive/`, and is a `DevelopmentDependency`. If you pack a library that source-builds a
  dependency, consumers of your package get the published binary as normal and see nothing of this.
- **Restore is untouched.** The dependency graph, transitive packages and `deps.json` are exactly
  what they were; only the assembly file changes.
- **Escape hatches.** `-p:NuGetToCompLogDisableSourceBuild=true` builds against the published
  binaries for one build. `-p:NuGetToCompLogAutoBuild=false` stops the build populating the cache
  and makes a missing assembly an error instead.
- **CI**: point `NUGET_TO_COMPLOG_CACHE` at a cached directory, alongside where you cache
  `~/.nuget/packages`, so the reconstruction is paid for once rather than per run.
