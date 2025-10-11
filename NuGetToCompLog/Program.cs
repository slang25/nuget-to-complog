using NuGetToCompLog;

if (args.Length < 1 || args[0] == "--help" || args[0] == "-h" || args[0] == "/?")
{
    Console.WriteLine("NuGet to CompLog - Extract compiler arguments from NuGet packages");
    Console.WriteLine();
    Console.WriteLine("Usage: NuGetToCompLog <package-id> [version]");
    Console.WriteLine();
    Console.WriteLine("Arguments:");
    Console.WriteLine("  package-id    The NuGet package identifier (e.g., Newtonsoft.Json)");
    Console.WriteLine("  version       Optional package version (e.g., 13.0.3). Defaults to latest stable.");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  NuGetToCompLog Newtonsoft.Json");
    Console.WriteLine("  NuGetToCompLog System.Text.Json 8.0.0");
    Console.WriteLine("  NuGetToCompLog Microsoft.Extensions.Logging");
    Console.WriteLine();
    Console.WriteLine("For more information, see README.md");
    return args.Length < 1 ? 1 : 0;
}

string packageId = args[0];
string? version = args.Length > 1 ? args[1] : null;

var extractor = new CompilerArgumentsExtractor();
await extractor.ProcessPackageAsync(packageId, version);

return 0;
