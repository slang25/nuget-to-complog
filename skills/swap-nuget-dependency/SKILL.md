---
name: swap-nuget-dependency
description: "Change what a NuGet dependency does, by recovering its original source and building it. `nuget-to-complog swap` replaces a project's PackageReference with a ProjectReference to that source in one command, so the dependency becomes editable C# that plain `dotnet build` compiles. Use this whenever you need to look inside or modify a dependency: validating a suspicion about what a library actually does, adding a log line or instrumentation inside a package, fixing a bug or applying a security hotfix you can't wait upstream for, changing a default, monkey-patching a library, working around a NuGet bug, or preparing a patch to submit upstream. Covers the standalone case too, where there is no consuming project to swap. Reach for this instead of decompiling, cloning the library's GitHub repo, or copying patched DLLs around — it is faster, more reliable, and far cheaper in tokens. Do NOT use it to build a dependency from source *without* changing it, for licensing or provenance reasons — that is source-build-nuget-package. Requires the nuget-to-complog tool."
metadata:
  version: dev
  checksum: dev
  source: nugettocomplog
---

# Swap a NuGet dependency for its source

`nuget-to-complog swap` replaces a project's `PackageReference` with a `ProjectReference` to the package's own recovered source. It downloads the package, reads the compiler metadata from its PDB, fetches the original source files via SourceLink, generates an SDK-style `.csproj` over them, and rewrites the consuming project — all in one command. From then on, ordinary `dotnet build` compiles the dependency from source.

Why this beats the alternatives:

- **Decompiling** gives you code you can read but not build, and it differs from what the author wrote. Swap gives you the *actual shipped source*, buildable.
- **Cloning the upstream repo** means finding the right tag, matching the build configuration, and wiring the project in by hand — slow, error-prone, and the repo build often needs tooling the consuming machine doesn't have. Swap recovers the source that produced the shipped assembly, verified against the PDB checksums.
- **Patched-DLL copying** breaks on every rebuild. After a swap, the normal build does the right thing, including for *other* packages that depend on the swapped one (NuGet's project-over-package rule substitutes your source-built project transitively).

## Prerequisites

- .NET SDK (10+)
- The `nuget-to-complog` global tool. Check with `nuget-to-complog --version`; install with:
  ```bash
  dotnet tool install -g nugettocomplog
  ```
- The target package must ship portable PDBs with SourceLink metadata (most modern open-source packages do).

## Workflow

### 1. Swap

Run from the directory where you want the `patches/` folder to live — typically the repo root:

```bash
nuget-to-complog swap <PackageId> [Version] [--project <path-to-csproj-or-dir>]
```

- Omit the version and it is resolved from the project file (`Version` attribute or element, `VersionOverride`) or from `Directory.Packages.props` under central package management.
- `--project` defaults to the single `.csproj` in the current directory.
- The ejected, buildable package project lands in `./patches/<PackageId>+<Version>/`, and the consuming `.csproj` gets its `PackageReference` rewritten to a `ProjectReference`. Only that one line changes — formatting, comments, and line endings are preserved, so `git diff` on the consuming project stays minimal.

### 2. Find and edit the code

The package's source is under `patches/<PackageId>+<Version>/src/`. This is the real source the shipped assembly was built from, so search it directly:

```bash
grep -rn "TheMethodYouSuspect" patches/<PackageId>+<Version>/src/
```

Edit in place. Read only the regions you need — these are files you already trust to be correct; you're making a targeted change, not reviewing them. New `.cs` files added under `src/` are compiled automatically (the generated project globs `src/**/*.cs`). Leave `.original/` untouched — it's the pristine copy that `diff` compares against.

### 3. Build and validate as usual

```bash
dotnet build   # or dotnet run / dotnet test on the consuming project
```

No DLL copying, no `--no-build` tricks. The dependency recompiles incrementally with your edits, and the consuming app exercises them directly. This is the "validate a suspicion" loop: add the instrumentation or fix, run the app or tests, observe.

### 4. Capture the change as a patch

```bash
nuget-to-complog diff <PackageId>
```

Run this from the same directory that contains `patches/` (or pass `--patches-dir`). It compares `src/` against `.original/` and writes a `git apply`-compatible unified diff to `patches/<PackageId>+<Version>.patch`.

The patch paths are relative to the recovered source root, not the upstream repo layout. Two mismatches are common: the upstream project usually sits in a subdirectory (Serilog's sources live under `src/Serilog/`), and the recovered tree can carry SourceLink prefixes like `_external/_/src/...`. If the patch is destined for an upstream checkout or PR, check its paths against the real repo layout first — strip any recovered-source prefix and either rewrite the paths or apply with `git apply --directory <subdir>`. Verify with `git apply --check` in the upstream checkout when one is available.

Commit only the `.patch` file; the ejected directories are regenerable:

```gitignore
patches/*/
!patches/*.patch
```

### 5. Undo

Revert the consuming project file and (optionally) delete the patch directory:

```bash
git checkout -- <path-to>/<Project>.csproj
rm -rf patches/<PackageId>+<Version>
```

## No consuming project? Eject instead

`swap` needs a project whose `PackageReference` it can rewrite. When there isn't one — you just
want a patched DLL, or you're preparing a change with nothing to test it in — use the standalone
path, which produces the same recovered source without touching any project:

```bash
nuget-to-complog eject <PackageId> [Version]     # recover source into patches/<PackageId>+<Version>/
# edit patches/<PackageId>+<Version>/src/
nuget-to-complog diff <PackageId>                # capture the edit as a .patch
nuget-to-complog apply <PackageId> --fetch-compiler
```

`apply` rebuilds through the original compiler response file rather than a generated `.csproj`, so
it stays closer to the original compilation than `swap` does — `--fetch-compiler` downloads the
exact Roslyn the package was built with when it isn't installed, leaving your patch as the only
intended difference from the shipped assembly. The rebuilt DLL lands in
`patches/<PackageId>+<Version>/bin/`.

Two things to watch on this path: a new `.cs` file has to be added to `build.rsp` by hand (the
response file lists sources explicitly), and getting the DLL into an app means copying it over the
one in `bin/` and running with `--no-build` so the next build doesn't overwrite it. That copying is
exactly what `swap` exists to avoid, so prefer `swap` whenever a consuming project exists.

## Things to know

- **The generated project approximates the original compilation.** It carries over language version, defines, optimization, nullable, and unsafe settings from the PDB, but it is not byte-exact. That's fine — for editing and validating, editable and incremental is the point. If you need a rebuild that stays closer to the original compilation, that path is `eject` + `apply --fetch-compiler` above.
- **Strong naming keeps working.** Strong-named packages are public-signed with the original key, so assembly identity and `InternalsVisibleTo` friendships survive the swap.
- **Your repo's build config won't leak in.** The patch directory gets stub `Directory.Build.props` / `Directory.Build.targets` / `Directory.Packages.props`, so the consuming repo's central package management, analyzers, and custom targets don't apply to the reconstructed project.
- **A few files may be decompiled stand-ins.** Recovery works through SourceLink; when a document can't be fetched, the tool decompiles it from the shipped assembly instead and says so in the swap output (`Decompiled N missing file(s)`). Stand-ins build and test fine, but they are not the author's text — if your edit lands in one, don't present that hunk upstream as authored source; locate the corresponding file in the upstream repo first.
- **Multi-targeting consumers:** every conditional `PackageReference` for the package is swapped. If different `ItemGroup`s pin different versions, pass the version explicitly to say which one to eject.
- **Packages shipping several assemblies** (e.g. `nunit.framework` + `nunit.framework.legacy`): the default is the assembly named after the package; pick another with `--assembly <name>.dll`.

## Troubleshooting

- **Swap fails on eject**: the package likely lacks portable PDBs or SourceLink (common for closed-source or very old packages). There is no source to recover — fall back to decompilation or the upstream repo.
- **Build errors after swap**: read the compiler errors — usually a missing conditional define or a dependency the nuspec didn't declare. Small fixes to the generated `.csproj` in the patch directory are fair game.
- **Version not found**: under central package management make sure `Directory.Packages.props` is reachable from the project; otherwise pass the version explicitly.
