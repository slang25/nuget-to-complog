using NuGetToCompLog.Abstractions;
using Spectre.Console;

namespace NuGetToCompLog.Infrastructure.Console;

/// <summary>
/// Spectre.Console implementation of console output.
/// </summary>
public class SpectreConsoleWriter : IConsoleWriter
{
    // ConEmu escape sequence for progress
    private const char ESC = '\x1b';
    private const char ST = '\x07'; // String Terminator (can also use \x1b\\)

    public void MarkupLine(string markup)
    {
        AnsiConsole.MarkupLine(markup);
    }

    public void WriteLine()
    {
        AnsiConsole.WriteLine();
    }

    public void WriteException(Exception exception)
    {
        AnsiConsole.WriteException(exception, ExceptionFormats.ShortenEverything);
    }

    public void WritePanel(string header, string content, string? borderColor = null)
    {
        var panel = new Panel(content)
            .Header($"[yellow]{header}[/]");

        if (borderColor != null)
        {
            try
            {
                // Try to parse the color name using reflection
                var colorProperty = typeof(Color).GetProperty(borderColor, 
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (colorProperty != null && colorProperty.GetValue(null) is Color color)
                {
                    panel.BorderColor(color);
                }
            }
            catch
            {
                // If color parsing fails, use default
            }
        }

        AnsiConsole.Write(panel);
    }

    public void WriteTree(string rootLabel, Dictionary<string, List<string>> nodes)
    {
        var tree = new Tree(rootLabel);

        foreach (var kvp in nodes)
        {
            var node = tree.AddNode(kvp.Key);
            foreach (var item in kvp.Value)
            {
                node.AddNode(item);
            }
        }

        AnsiConsole.Write(tree);
    }

    public void WriteTable(string[] headers, List<string[]> rows)
    {
        var table = new Table()
            .BorderColor(Color.Grey);

        foreach (var header in headers)
        {
            table.AddColumn(header);
        }

        foreach (var row in rows)
        {
            table.AddRow(row);
        }

        AnsiConsole.Write(table);
    }

    public async Task ExecuteWithStatusAsync(string status, Func<Task> action)
    {
        // Set indeterminate progress state (spinning)
        SetConEmuProgress(3, 0);

        try
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("cyan"))
                .StartAsync(status, async ctx => await action());
        }
        finally
        {
            // Remove progress state
            SetConEmuProgress(0, 0);
        }
    }

    /// <summary>
    /// Set ConEmu/Windows Terminal progress state.
    /// </summary>
    /// <param name="state">
    /// 0: Remove progress
    /// 1: Set progress value (use with progress parameter)
    /// 2: Set error state
    /// 3: Set indeterminate state (spinning)
    /// 4: Set paused state
    /// </param>
    /// <param name="progress">Progress value 0-100 (used when state is 1)</param>
    private void SetConEmuProgress(int state, int progress)
    {
        try
        {
            // ESC ] 9 ; 4 ; st ; pr ST
            System.Console.Write($"{ESC}]9;4;{state};{progress}{ST}");
        }
        catch
        {
            // Ignore errors - terminal might not support ConEmu sequences
        }
    }

    /// <summary>
    /// Set specific progress value (0-100).
    /// </summary>
    public void SetProgress(int value)
    {
        if (value < 0 || value > 100)
            throw new ArgumentOutOfRangeException(nameof(value), "Progress must be between 0 and 100");
        
        SetConEmuProgress(1, value);
    }

    /// <summary>
    /// Set indeterminate (spinning) progress.
    /// </summary>
    public void SetIndeterminateProgress()
    {
        SetConEmuProgress(3, 0);
    }

    /// <summary>
    /// Set error progress state.
    /// </summary>
    public void SetErrorProgress()
    {
        SetConEmuProgress(2, 0);
    }

    /// <summary>
    /// Clear/remove progress indicator.
    /// </summary>
    public void ClearProgress()
    {
        SetConEmuProgress(0, 0);
    }
}
