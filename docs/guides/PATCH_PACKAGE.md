# Patch Package Guide

Patch any NuGet package at the source level. The `eject`, `diff`, and `apply` commands let you recover the original source code from a published NuGet package, make edits, and rebuild a patched assembly — no upstream fork required.

This is inspired by [patch-package](https://github.com/ds300/patch-package) from the npm ecosystem.

## Use cases

- **Bug fixes you can't wait for.** A dependency has a known bug and the maintainer hasn't released a fix yet. Patch the source and ship a fixed build while you wait.
- **Behaviour tweaks.** You need a small change to a library's internals — a different default, a removed check, an added log line — that doesn't warrant a full fork.
- **Debugging production issues.** Add instrumentation or logging to a third-party library to diagnose a problem in your environment.
- **Security hotfixes.** Apply a targeted fix to a vulnerable dependency before an official patch is available.
- **Experimentation.** Try out changes to a library to validate an approach before submitting a PR upstream.

## Prerequisites

- .NET 10 SDK or later
- The target NuGet package must ship with portable PDBs (embedded or via a `.snupkg` symbols package) that contain SourceLink information. Most modern, open-source NuGet packages meet this requirement.

## Workflow

The workflow has four steps: **eject**, **edit**, **diff**, **apply**.

### 1. Eject the package

```bash
dotnet run -- eject <PackageId> [Version]
```

This downloads the NuGet package, reads compiler metadata from the PDB, downloads the original source files via SourceLink, and creates an editable project under `./patches/<PackageId>+<Version>/`.

```bash
# Eject a specific version
dotnet run -- eject Newtonsoft.Json 13.0.3

# Eject the latest version
dotnet run -- eject Newtonsoft.Json

# Eject to a custom directory
dotnet run -- eject Newtonsoft.Json 13.0.3 --output ./my-patches
```

The ejected directory structure:

```
patches/Newtonsoft.Json+13.0.3/
  src/                  # Editable source files (your working copy)
  .original/            # Pristine copy of the original source
  refs/                 # Reference assemblies needed for compilation
  bin/                  # Output directory for rebuilt assemblies
  build.rsp             # Compiler response file for csc
  rebuild.sh            # Shell script to rebuild manually
  rebuild.cmd           # Windows batch script to rebuild manually
  patch-metadata.json   # Package identity and build metadata
```

### 2. Edit the source

Open any file under `src/` and make your changes. The `.original/` directory is kept as a clean reference — don't modify it.

```bash
# Example: edit a file in the ejected source
$EDITOR patches/Newtonsoft.Json+13.0.3/src/JsonConvert.cs
```

### 3. Create a patch

```bash
dotnet run -- diff <PackageId>
```

This compares `src/` against `.original/` and writes a unified diff to `patches/<PackageId>+<Version>.patch`. The patch format is compatible with `git apply`.

```bash
dotnet run -- diff Newtonsoft.Json
```

You can inspect the generated patch file:

```bash
cat patches/Newtonsoft.Json+13.0.3.patch
```

### 4. Apply and rebuild

```bash
dotnet run -- apply [PackageId]
```

This applies the patch to the original source, then invokes the C# compiler (`csc.dll` via `dotnet exec`) with the response file to produce a rebuilt assembly in `bin/`.

```bash
# Apply patches for a specific package
dotnet run -- apply Newtonsoft.Json

# Apply all patches
dotnet run -- apply
```

The rebuilt DLL appears at `patches/<PackageId>+<Version>/bin/<AssemblyName>.dll`.

### 5. Use the patched assembly

Copy the rebuilt DLL over the original in your project's build output:

```bash
# Build your project first
dotnet build

# Swap in the patched DLL
cp patches/Newtonsoft.Json+13.0.3/bin/Newtonsoft.Json.dll \
   MyApp/bin/Debug/net8.0/Newtonsoft.Json.dll

# Run without rebuilding (so the swap isn't overwritten)
dotnet run --project MyApp --no-build
```

For a more permanent setup, you could add a post-build step to your `.csproj` that copies the patched DLL into your output directory.

## Two apply modes

The `apply` command supports two workflows:

**Patch file mode** (recommended for teams): Run `diff` first to create a `.patch` file, then `apply` reads the patch, applies it to `.original/`, and rebuilds. The `.patch` file is small, human-readable, and easy to commit to source control.

**Direct edit mode**: If no `.patch` file exists, `apply` builds directly from the `src/` directory. This is simpler for quick one-off fixes where you don't need a portable patch file.

## Example walkthrough

Here's a complete example that patches `Newtonsoft.Json` to prepend a marker string to serialized output:

```bash
# 1. Create a test project
dotnet new console -n DemoApp -o DemoApp
cd DemoApp
dotnet add package Newtonsoft.Json --version 13.0.3

# Write a simple program
cat > Program.cs << 'EOF'
using Newtonsoft.Json;
var obj = new { name = "world", patched = false };
Console.WriteLine(JsonConvert.SerializeObject(obj));
EOF

# Run it — outputs: {"name":"world","patched":false}
dotnet run

# 2. Eject the package (run from the parent directory)
cd ..
dotnet run --project path/to/nuget-to-complog/src/NuGetToCompLog -- eject Newtonsoft.Json 13.0.3

# 3. Edit the source
# In patches/Newtonsoft.Json+13.0.3/src/JsonConvert.cs, find:
#   public static string SerializeObject(object? value)
#   {
#       return SerializeObject(value, null, (JsonSerializerSettings?)null);
#   }
# Change the return to:
#   return "/* PATCHED */ " + SerializeObject(value, null, (JsonSerializerSettings?)null);

# 4. Create and apply the patch
dotnet run --project path/to/nuget-to-complog/src/NuGetToCompLog -- diff Newtonsoft.Json
dotnet run --project path/to/nuget-to-complog/src/NuGetToCompLog -- apply Newtonsoft.Json

# 5. Swap and run
cd DemoApp
dotnet build
cp ../patches/Newtonsoft.Json+13.0.3/bin/Newtonsoft.Json.dll \
   bin/Debug/net8.0/Newtonsoft.Json.dll
dotnet run --no-build
# Outputs: /* PATCHED */ {"name":"world","patched":false}
```

## How it works under the hood

1. **Eject** downloads the `.nupkg` and `.snupkg`, reads the portable PDB to extract compiler arguments (defines, language version, optimization level, etc.) and the SourceLink mapping, then downloads all original source files from the repository at the exact commit they were built from.

2. **Diff** uses an LCS-based algorithm to generate standard unified diffs between `.original/` and `src/`.

3. **Apply** invokes the C# compiler directly via `dotnet exec <sdk>/Roslyn/bincore/csc.dll @build.rsp`. The `build.rsp` response file contains all the compiler flags, source file paths, and reference assembly paths needed to produce the output DLL — no MSBuild or `.csproj` involved.

## Limitations

- **Requires SourceLink and portable PDBs.** Packages that don't publish PDBs (or only publish Windows PDBs) or don't include SourceLink metadata cannot be ejected. Most modern open-source NuGet packages meet this requirement, but older or closed-source packages may not.

- **Source recovery is best-effort.** Some packages embed only a subset of source files. If the PDB doesn't reference a source file (e.g. generated code that's compiled but not tracked), it won't be recovered via SourceLink.

- **No MSBuild integration.** The rebuild uses `csc.dll` directly with a response file. Source generators, analyzers, and custom MSBuild targets from the original build are not replicated. If the package relies heavily on source generators, the rebuilt assembly may differ or fail to compile.

- **Single TFM rebuild.** The tool selects the best target framework from the package and rebuilds only that one. Multi-TFM packages will only have one framework's assembly rebuilt.

- **Assembly identity may differ.** The rebuilt assembly won't be strong-name signed (even if the original was), and the MVID and timestamps will differ. This means:
  - Packages that rely on `InternalsVisibleTo` with strong naming won't work with the patched assembly.
  - Assembly binding redirects may need adjustment.

- **Reference assembly resolution.** The tool attempts to acquire all referenced assemblies from the local SDK and NuGet cache. If a reference can't be resolved, the rebuild will fail with compiler errors.

- **DLL swap is manual.** After rebuilding, you need to manually copy the patched DLL into your project's output directory. The runtime loads the DLL from the build output, so you need to either use `--no-build` or add a post-build copy step to prevent the original from being restored.

- **Compiler version mismatch.** The rebuild uses whatever `csc.dll` ships with your locally installed .NET SDK, which may differ from the compiler version used to originally build the package. This can occasionally cause warnings or subtle differences in the output.

## Committing patches to source control

The recommended approach is to commit the `.patch` file to your repository:

```
patches/
  Newtonsoft.Json+13.0.3.patch
```

The `.patch` file is small (just the diff) and human-readable. The ejected source, references, and built output should be `.gitignore`d — they can be regenerated by running `eject` and `apply`.

Example `.gitignore`:

```gitignore
patches/*/
!patches/*.patch
```
