# ConsoleAppFramework - Positional Arguments Fix

## Issue

When running `dotnet run -- JustSaying 8.0.0`, the error was:
```
Argument '8.0.0' is not recognized
```

## Root Cause

The `version` parameter wasn't marked with `[Argument]` attribute, so ConsoleAppFramework treated it as a named option (`--version`) instead of a positional argument.

## Solution

Mark both parameters with `[Argument]` to make them positional:

```csharp
[Command("")]
public async Task Process(
    [Argument] string packageId,      // Required positional [0]
    [Argument] string? version = null) // Optional positional [1]
{
    // ...
}
```

## Result

Now all these work correctly:

```bash
# With version (positional)
dotnet run -- JustSaying 8.0.0

# Without version (uses latest)
dotnet run -- JustSaying

# Help
dotnet run -- --help
```

## Help Output

```
Usage: [arguments...] [-h|--help] [--version]

Process a NuGet package and create a CompLog file.

Arguments:
  [0] <string>     The NuGet package identifier (e.g., Newtonsoft.Json)
  [1] <string?>    The package version (e.g., 13.0.3). If not specified, uses latest stable version.
```

## ConsoleAppFramework Behavior

- **Without `[Argument]`**: Parameter becomes a named option (--parameter-name)
- **With `[Argument]`**: Parameter becomes a positional argument [index]
- **With `= null`**: Optional parameter (can be omitted)
- **Without default**: Required parameter (must be provided)

## Verified Working

✅ Tested with `JustSaying 8.0.0`
✅ Successfully processed package
✅ Created CompLog with 195 source files
✅ All 170 reference assemblies acquired

---

**Fixed**: 2025-10-13
**Status**: ✅ Working
