# ConEmu Progress Integration

## Overview

The `SpectreConsoleWriter` now supports ConEmu/Windows Terminal progress indicators using ANSI escape sequences. This provides visual feedback in the terminal title bar and taskbar (on Windows 7+).

## What is ConEmu Progress?

ConEmu is a Windows console emulator that supports special escape sequences for displaying progress indicators. These sequences are also supported by:
- Windows Terminal
- ConEmu
- Some other modern terminal emulators

The progress appears as:
- **Title bar**: Progress indicator in the terminal title
- **Taskbar** (Windows 7+): Progress bar on the taskbar icon

## Escape Sequence Format

```
ESC ] 9 ; 4 ; state ; progress ST
```

Where:
- `ESC` = `\x1b` (escape character)
- `ST` = `\x07` (string terminator)
- `state`:
  - `0` - Remove progress
  - `1` - Set progress value (0-100)
  - `2` - Set error state
  - `3` - Set indeterminate state (spinning/busy)
  - `4` - Set paused state
- `progress` - Value from 0-100 (only used with state 1)

## Implementation

### Automatic Progress in ExecuteWithStatusAsync

The `ExecuteWithStatusAsync` method now automatically:
1. Sets **indeterminate progress** (spinning) when starting
2. Shows a Spectre.Console spinner in the terminal
3. Clears progress when complete

```csharp
await _console.ExecuteWithStatusAsync("Downloading package...", async () =>
{
    // Your async work here
    // Progress indicator shows spinning/busy state
});
// Progress automatically cleared after completion
```

### Manual Progress Control

You can also manually control progress:

```csharp
var console = new SpectreConsoleWriter();

// Set specific progress value
console.SetProgress(25);  // 25%
console.SetProgress(50);  // 50%
console.SetProgress(100); // 100%

// Set indeterminate (spinning) state
console.SetIndeterminateProgress();

// Set error state
console.SetErrorProgress();

// Clear/remove progress
console.ClearProgress();
```

## Public API

### SpectreConsoleWriter Methods

```csharp
// Set progress value (0-100)
public void SetProgress(int value)

// Set indeterminate (spinning) progress
public void SetIndeterminateProgress()

// Set error progress state
public void SetErrorProgress()

// Clear/remove progress indicator
public void ClearProgress()

// Existing method - now with auto progress
public async Task ExecuteWithStatusAsync(string status, Func<Task> action)
```

## Visual Feedback

### Windows Terminal
```
┌─ Terminal Title ──────────────────────────┐
│ ⣿⣿⣿⣿⣿⣿⣿⣿⣿⣿░░░░░░░░░░ 50%              │
│                                          │
│ Processing package...                    │
└──────────────────────────────────────────┘
```

### Taskbar (Windows 7+)
```
┌──────────────┐
│ [■■■■■░░░░░] │  ← Taskbar icon shows progress
│  Terminal    │
└──────────────┘
```

## Example Usage in Code

### Simple Progress

```csharp
// Show spinning progress during long operation
await _console.ExecuteWithStatusAsync("Downloading...", async () =>
{
    await DownloadFileAsync();
});
```

### Detailed Progress

```csharp
// Manual progress reporting
_console.SetProgress(0);
for (int i = 0; i < 100; i++)
{
    await ProcessItemAsync(i);
    _console.SetProgress(i + 1);
}
_console.ClearProgress();
```

### Error Handling

```csharp
try
{
    _console.SetIndeterminateProgress();
    await ProcessAsync();
    _console.ClearProgress();
}
catch (Exception ex)
{
    _console.SetErrorProgress();
    await Task.Delay(1000); // Show error state briefly
    _console.ClearProgress();
    throw;
}
```

## Terminal Compatibility

| Terminal | Support | Notes |
|----------|---------|-------|
| Windows Terminal | ✅ Yes | Full support |
| ConEmu | ✅ Yes | Original implementation |
| Windows Console Host | ⚠️ Partial | Ignores sequences (no error) |
| macOS Terminal | ❌ No | Ignores sequences (no error) |
| iTerm2 | ❌ No | Ignores sequences (no error) |
| VS Code Terminal | ⚠️ Varies | Depends on platform |

**Note**: The implementation gracefully handles terminals that don't support these sequences - they simply ignore them with no errors.

## Benefits

1. **Better UX**: Users see progress in taskbar/title without focusing on terminal
2. **No Breaking Changes**: Existing code continues to work
3. **Automatic**: `ExecuteWithStatusAsync` enables it automatically
4. **Graceful Degradation**: Works on all terminals (ignores sequences if not supported)
5. **Zero Dependencies**: Uses standard ANSI escape sequences

## Testing

The feature was tested with:
- ✅ Windows Terminal (full support)
- ✅ macOS Terminal (gracefully ignored)
- ✅ VS Code integrated terminal (works on Windows)

## Implementation Details

### Escape Sequence Format

```csharp
private const char ESC = '\x1b';  // Escape character
private const char ST = '\x07';   // String Terminator

private void SetConEmuProgress(int state, int progress)
{
    try
    {
        // ESC ] 9 ; 4 ; state ; progress ST
        System.Console.Write($"{ESC}]9;4;{state};{progress}{ST}");
    }
    catch
    {
        // Ignore errors - terminal might not support ConEmu sequences
    }
}
```

### Error Handling

The implementation wraps all escape sequence writes in try-catch blocks to ensure:
- No exceptions are thrown on unsupported terminals
- Application continues normally even if terminal doesn't recognize sequences
- Silent degradation for better compatibility

## References

- [ConEmu ANSI Sequences](https://conemu.github.io/en/AnsiEscapeCodes.html)
- [Windows Terminal Sequences](https://docs.microsoft.com/en-us/windows/terminal/tutorials/shell-integration)
- [ANSI Escape Codes](https://en.wikipedia.org/wiki/ANSI_escape_code)

## Future Enhancements

Potential future additions:
- [ ] Add progress bar in console (in addition to title/taskbar)
- [ ] Support for percentage text in title
- [ ] Configurable progress styles
- [ ] Integration with `IProgress<T>` interface

---

**Added**: 2025-10-13  
**Status**: ✅ Production Ready  
**Breaking Changes**: None
