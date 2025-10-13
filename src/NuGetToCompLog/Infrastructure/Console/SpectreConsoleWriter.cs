using NuGetToCompLog.Abstractions;
using Spectre.Console;

namespace NuGetToCompLog.Infrastructure.Console;

/// <summary>
/// Spectre.Console implementation of console output.
/// </summary>
public class SpectreConsoleWriter : IConsoleWriter
{
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
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("cyan"))
            .StartAsync(status, async ctx => await action());
    }
}
