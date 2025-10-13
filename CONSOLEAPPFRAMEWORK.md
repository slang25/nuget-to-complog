# ConsoleAppFramework Integration

## Overview

NuGet to CompLog now uses [ConsoleAppFramework](https://github.com/Cysharp/ConsoleAppFramework) v5 for CLI argument parsing and command execution. This provides:

- 🚀 **Zero Overhead** - Source generator-based with no reflection
- ⚡ **Fast Startup** - Minimal initialization time
- 📝 **Auto-generated Help** - Documentation from XML comments
- 🎯 **Type Safety** - Compile-time validation of commands
- 🔄 **DI Integration** - Works seamlessly with Microsoft.Extensions.DependencyInjection

## Command Structure

### Main Command

```bash
# Process a package
dotnet run -- <package-id> [options]

# With version
dotnet run -- Newtonsoft.Json --version 13.0.3

# Short form
dotnet run -- Newtonsoft.Json -v 13.0.3

# Get help
dotnet run -- --help
```

### Command Implementation

Commands are defined as methods in `NuGetCommands` class:

```csharp
public class NuGetCommands
{
    [Command("")]  // Root command
    public async Task Process(
        [Argument] string packageId,           // Positional argument
        string? version = null,                // Optional named argument
        CancellationToken cancellationToken = default)
    {
        // Command implementation
    }
}
```

## Help Output

ConsoleAppFramework automatically generates help from XML documentation comments:

```
Usage: [arguments...] [options...] [-h|--help] [--version]

Process a NuGet package and create a CompLog file.

Arguments:
  [0] <string>    The NuGet package identifier (e.g., Newtonsoft.Json)

Options:
  -v|--version <string?>    The package version (e.g., 13.0.3). 
                           If not specified, uses latest stable version. 
                           (Default: null)
```

## Architecture Integration

### Program.cs Setup

```csharp
// Display header
AnsiConsole.Write(new FigletText("NuGet to CompLog")...);

// Set up DI
var services = new ServiceCollection();
services.AddNuGetToCompLogServices();

// Build ConsoleApp
var app = ConsoleApp.Create();
app.Add<NuGetCommands>();

// Run with DI
await using var serviceProvider = services.BuildServiceProvider();
ConsoleApp.ServiceProvider = serviceProvider;
await app.RunAsync(args);
```

### Command Class with DI

Commands receive dependencies via constructor injection:

```csharp
public class NuGetCommands
{
    private readonly ProcessPackageCommandHandler _handler;

    // Dependencies injected by ConsoleAppFramework
    public NuGetCommands(ProcessPackageCommandHandler handler)
    {
        _handler = handler;
    }

    [Command("")]
    public async Task Process(...)
    {
        var command = new ProcessPackageCommand(packageId, version);
        await _handler.HandleAsync(command, cancellationToken);
    }
}
```

## Features Used

### 1. Argument Attributes

- `[Argument]` - Positional argument (required)
- `string? version = null` - Optional named parameter

### 2. Automatic Help

Help is auto-generated from:
- XML documentation comments (`/// <summary>`)
- Parameter names
- Default values
- Parameter descriptions (`/// <param>`)

### 3. Cancellation Support

Built-in `CancellationToken` support for graceful shutdown:

```csharp
public async Task Process(
    [Argument] string packageId,
    string? version = null,
    CancellationToken cancellationToken = default)  // Auto-wired
{
    // Use cancellationToken in async operations
}
```

### 4. Error Handling

Exceptions are caught and displayed with exit code 1:

```csharp
if (result == null)
{
    throw new Exception("Failed to process package");  // Auto-handled
}
```

## Benefits Over Previous Implementation

### Before (Manual Parsing)

```csharp
if (args.Length < 1 || args[0] == "--help")
{
    DisplayHelp();  // Manual help implementation
    return args.Length < 1 ? 1 : 0;
}

string packageId = args[0];
string? version = args.Length > 1 ? args[1] : null;
```

**Issues**:
- Manual argument parsing
- Manual help text
- No validation
- No type safety
- Fragile to changes

### After (ConsoleAppFramework)

```csharp
[Command("")]
public async Task Process(
    [Argument] string packageId,
    string? version = null,
    CancellationToken cancellationToken = default)
```

**Benefits**:
- ✅ Automatic parsing
- ✅ Generated help
- ✅ Type validation
- ✅ Named parameters support
- ✅ Compile-time safety

## Adding New Commands

To add a new command:

1. Add a method to `NuGetCommands`:

```csharp
/// <summary>
/// Verify a complog file.
/// </summary>
/// <param name="file">The complog file path</param>
[Command("verify")]
public async Task Verify([Argument] string file)
{
    // Implementation
}
```

2. Use it:

```bash
dotnet run -- verify mypackage.complog
```

That's it! ConsoleAppFramework handles the rest.

## Performance

ConsoleAppFramework v5 uses source generators to achieve:
- **Zero reflection** - All parsing generated at compile time
- **Zero allocation** - Minimal GC pressure
- **Fast startup** - No runtime initialization
- **Small binary** - No framework dependencies

Perfect for CLI tools that need fast cold-start performance.

## Advanced Features

### Multiple Commands

```csharp
public class NuGetCommands
{
    [Command("")]
    public Task Process(...) { }

    [Command("verify")]
    public Task Verify(...) { }

    [Command("list")]
    public Task List(...) { }
}
```

### Custom Validation

```csharp
public async Task Process([Argument] string packageId, ...)
{
    if (string.IsNullOrWhiteSpace(packageId))
    {
        throw new ValidationException("Package ID cannot be empty");
    }
    // ...
}
```

### Filters (Middleware)

ConsoleAppFramework supports filters for cross-cutting concerns:

```csharp
app.UseFilter<LoggingFilter>();
app.UseFilter<ExceptionHandlingFilter>();
```

## Migration Notes

The migration from manual parsing to ConsoleAppFramework:
- **Maintained**: All existing functionality
- **Improved**: Help output now auto-generated
- **Added**: Type safety and validation
- **Removed**: ~40 lines of manual argument parsing code

## Documentation

- [ConsoleAppFramework GitHub](https://github.com/Cysharp/ConsoleAppFramework)
- [Source Generator Documentation](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/source-generators-overview)

## Summary

ConsoleAppFramework integration provides:
- Modern CLI framework with zero overhead
- Beautiful auto-generated help
- Type-safe command definitions
- Seamless DI integration
- Production-ready error handling
- Easy to extend with new commands

All while maintaining the clean architecture and testability of the refactored codebase!
