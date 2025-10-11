using NuGetToCompLog;
using Spectre.Console;

// Display fancy header
AnsiConsole.Write(
    new FigletText("NuGet to CompLog")
        .LeftJustified()
        .Color(Color.Cyan1));

if (args.Length < 1 || args[0] == "--help" || args[0] == "-h" || args[0] == "/?")
{
    var table = new Table()
        .BorderColor(Color.Grey)
        .AddColumn(new TableColumn("[yellow]Usage[/]").Centered());
    
    table.AddRow("[cyan]NuGetToCompLog[/] [green]<package-id>[/] [blue][[version]][/]");
    AnsiConsole.Write(table);
    
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[yellow]Arguments:[/]");
    var argsTable = new Table()
        .BorderColor(Color.Grey)
        .AddColumn("Argument")
        .AddColumn("Description");
    
    argsTable.AddRow("[green]package-id[/]", "The NuGet package identifier (e.g., Newtonsoft.Json)");
    argsTable.AddRow("[blue]version[/]", "Optional package version (e.g., 13.0.3). Defaults to latest stable.");
    AnsiConsole.Write(argsTable);
    
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[yellow]Examples:[/]");
    AnsiConsole.MarkupLine("  [dim]$[/] [cyan]NuGetToCompLog[/] [green]Newtonsoft.Json[/]");
    AnsiConsole.MarkupLine("  [dim]$[/] [cyan]NuGetToCompLog[/] [green]System.Text.Json[/] [blue]8.0.0[/]");
    AnsiConsole.MarkupLine("  [dim]$[/] [cyan]NuGetToCompLog[/] [green]Microsoft.Extensions.Logging[/]");
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[dim]For more information, see README.md[/]");
    return args.Length < 1 ? 1 : 0;
}

string packageId = args[0];
string? version = args.Length > 1 ? args[1] : null;

var extractor = new CompilerArgumentsExtractor();
await extractor.ProcessPackageAsync(packageId, version);

return 0;
