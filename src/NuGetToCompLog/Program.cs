using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using NuGetToCompLog;
using NuGetToCompLog.Cli;
using Spectre.Console;

var banner = @"
  [cyan1]███╗   ██╗ ██████╗ ██████╗  ██████╗██╗     [/]
  [cyan1]████╗  ██║██╔════╝ ╚════██╗██╔════╝██║     [/]
  [cyan1]██╔██╗ ██║██║  ███╗ █████╔╝██║     ██║     [/]
  [cyan1]██║╚██╗██║██║   ██║██╔═══╝ ██║     ██║     [/]
  [cyan1]██║ ╚████║╚██████╔╝███████╗╚██████╗███████╗[/]
  [cyan1]╚═╝  ╚═══╝ ╚═════╝ ╚══════╝ ╚═════╝╚══════╝[/]
  [dim]NuGet → CompLog Extractor[/]
";
// The skill command's default mode prints SKILL.md to stdout for piping; keep the banner out
// of that stream.
if (args is not ["skill", ..])
{
    AnsiConsole.MarkupLine(banner);
}

var services = new ServiceCollection();
services.AddNuGetToCompLogServices();

var app = ConsoleApp.Create();
app.Add<NuGetCommands>();

await using var serviceProvider = services.BuildServiceProvider();
ConsoleApp.ServiceProvider = serviceProvider;

await app.RunAsync(args);
