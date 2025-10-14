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
AnsiConsole.MarkupLine(banner);

var services = new ServiceCollection();
services.AddNuGetToCompLogServices();

var app = ConsoleApp.Create();
app.Add<NuGetCommands>();

await using var serviceProvider = services.BuildServiceProvider();
ConsoleApp.ServiceProvider = serviceProvider;

await app.RunAsync(args);
