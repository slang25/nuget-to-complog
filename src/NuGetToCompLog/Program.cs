using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using NuGetToCompLog;
using NuGetToCompLog.Cli;
using Spectre.Console;

// Display fancy header
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

// Set up dependency injection
var services = new ServiceCollection();
services.AddNuGetToCompLogServices();

// Build the ConsoleApp with DI
var app = ConsoleApp.Create();
app.Add<NuGetCommands>();

// Run with DI container
await using var serviceProvider = services.BuildServiceProvider();
ConsoleApp.ServiceProvider = serviceProvider;

await app.RunAsync(args);
