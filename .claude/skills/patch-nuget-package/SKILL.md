---
name: patch-nuget-package
description: Patch a NuGet package at the source level — eject its original source code, make edits, and rebuild a patched DLL. Use this skill whenever you need to modify a NuGet dependency's behavior: fixing a bug, adding logging/instrumentation, tweaking defaults, applying a security hotfix, or validating a change before submitting it upstream. Also use it when the user mentions "patching a package", "editing a NuGet dependency", "monkey-patching a library", "working around a NuGet bug", or wants to prepare a change against an upstream .NET library.
---

# Patch NuGet Package

Patch any NuGet package at the source level. Recover the original source from a published package, edit it, and rebuild a patched assembly — no upstream fork required.

This is the .NET equivalent of [patch-package](https://github.com/ds300/patch-package) from npm.

## When to use this

- A dependency has a bug and you can't wait for an upstream fix
- You need a small behavior change (different default, removed check, added log line)
- You want to add instrumentation to debug a production issue
- You need a targeted security hotfix before the official patch
- You want to validate a change before submitting a PR upstream

## Prerequisites

- .NET 10 SDK or later
- The target package must ship portable PDBs with SourceLink metadata (most modern open-source NuGet packages do)

## The tool

All commands are run via this repo's CLI tool. `$TOOL` refers to the project path relative to the repo root:

```
src/NuGetToCompLog
```

Invoke commands as:
```bash
dotnet run --project src/NuGetToCompLog -- <command> [args]
```

## Workflow

### Step 1: Eject the package

```bash
dotnet run --project src/NuGetToCompLog -- eject <PackageId> [Version] [--output <directory>]
```

This downloads the NuGet package, reads compiler metadata from the PDB, downloads original source files via SourceLink, and creates an editable project.

- If you omit the version, it uses the latest stable release
- Default output is `./patches/<PackageId>+<Version>/`
- The `--output` flag overrides the base patches directory

The ejected directory contains:
```
patches/<PackageId>+<Version>/
  src/                  # Editable source files — make changes here
  .original/            # Pristine original source — do NOT modify
  refs/                 # Reference assemblies for compilation
  bin/                  # Output directory for rebuilt assemblies
  build.rsp             # Compiler response file
  patch-metadata.json   # Package identity and build metadata
```

### Step 2: Edit the source

Open files under `src/` and make changes. The `.original/` directory is the clean reference — leave it untouched.

When making edits:
- Read the file first to understand the existing code
- Make targeted, minimal changes
- If the goal is to prepare an upstream PR, keep changes clean and well-motivated
- **If you add a new source file**, you must also add it to `build.rsp` in the ejected directory — the compiler response file explicitly lists all source files, so new files won't be compiled unless they're listed there

### Step 3: Create a patch file

```bash
dotnet run --project src/NuGetToCompLog -- diff <PackageId> [--patches-dir <directory>]
```

This compares `src/` against `.original/` and writes a unified diff to `patches/<PackageId>+<Version>.patch`. The patch format is compatible with `git apply`.

The version is auto-detected if only one version is ejected for that package.

### Step 4: Apply and rebuild

```bash
dotnet run --project src/NuGetToCompLog -- apply [PackageId] [--patches-dir <directory>]
```

This applies the patch to the original source and invokes the C# compiler (`csc.dll` via `dotnet exec`) with the response file to produce a rebuilt assembly.

- If a `.patch` file exists, it applies from that (recommended for reproducibility)
- If no `.patch` file exists, it compiles directly from the edited `src/` directory
- Omit `PackageId` to apply all patches

The rebuilt DLL appears at `patches/<PackageId>+<Version>/bin/<AssemblyName>.dll`.

### Step 5: Use the patched assembly

After rebuilding, swap the patched DLL into the project's build output:

```bash
# Build the consuming project first
dotnet build

# Copy the patched DLL over the original
cp patches/<PackageId>+<Version>/bin/<AssemblyName>.dll \
   <ProjectName>/bin/Debug/net10.0/<AssemblyName>.dll

# Run without rebuilding so the swap isn't overwritten
dotnet run --project <ProjectName> --no-build
```

For a permanent setup, suggest adding a post-build MSBuild target to the `.csproj`.

## Complete example

Here's the full flow for patching `Newtonsoft.Json`:

```bash
# 1. Eject
dotnet run --project src/NuGetToCompLog -- eject Newtonsoft.Json 13.0.3

# 2. Edit src/JsonConvert.cs (make your changes)

# 3. Create the patch
dotnet run --project src/NuGetToCompLog -- diff Newtonsoft.Json

# 4. Apply and rebuild
dotnet run --project src/NuGetToCompLog -- apply Newtonsoft.Json

# 5. The patched DLL is at:
#    patches/Newtonsoft.Json+13.0.3/bin/Newtonsoft.Json.dll
```

## Things to know

- **Patch files are small and committable.** The `.patch` file is a standard unified diff — commit it to source control. The ejected directories can be regenerated.
- **Assembly won't be strong-name signed.** If the original was signed, binding redirects may need adjustment.
- **Single TFM only.** The tool rebuilds one target framework. For multi-TFM packages, only the best match is rebuilt.
- **No MSBuild/source generators.** The rebuild uses `csc.dll` directly. Packages relying on source generators may not rebuild correctly.
- **Compiler version may differ.** The rebuild uses the local SDK's `csc.dll`, which may differ from the version used to build the package originally.
- **SourceLink required.** Packages without portable PDBs or SourceLink metadata cannot be ejected.

## Recommended .gitignore for patches

```gitignore
patches/*/
!patches/*.patch
```

This commits only the `.patch` files and ignores the ejected directories.

## Troubleshooting

If eject fails:
- Check that the package has portable PDBs (not Windows PDBs)
- Verify the package has SourceLink configured
- Try a specific version rather than latest

If rebuild fails:
- Check compiler errors in the output — usually a missing reference
- Verify the .NET 10 SDK is installed
- Look at `build.rsp` to understand what compiler flags are being used
