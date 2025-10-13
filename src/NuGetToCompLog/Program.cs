using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using NuGetToCompLog;
using NuGetToCompLog.Cli;
using Spectre.Console;

// Display fancy header
AnsiConsole.Write(
    new FigletText("NuGet to CompLog")
        .LeftJustified()
        .Color(Color.Cyan1));

AnsiConsole.WriteLine();

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
