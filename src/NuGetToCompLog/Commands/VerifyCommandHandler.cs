using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Services;
using NuGetToCompLog.Services.SourceBuild;
using NuGetToCompLog.Services.Verify;

namespace NuGetToCompLog.Commands;

/// <summary>
/// Proves (or disproves) that a package round-trips: creates a complog, rebuilds from it with
/// the exact compiler version recorded in the PDB, and byte-compares the result against the
/// assembly shipped in the package.
/// </summary>
public class VerifyCommandHandler
{
    private readonly PackageAnalysisPipeline _pipeline;
    private readonly ComplogRebuilder _rebuilder;
    private readonly IConsoleWriter _console;

    public VerifyCommandHandler(
        PackageAnalysisPipeline pipeline,
        ComplogRebuilder rebuilder,
        IConsoleWriter console)
    {
        _pipeline = pipeline;
        _rebuilder = rebuilder;
        _console = console;
    }

    /// <returns>0 = byte-for-byte match, 2 = content match with derived-field drift, 1 = real differences or failure.</returns>
    public async Task<int> HandleAsync(
        string packageId,
        string? version,
        bool fetchCompiler = false,
        string? assembly = null,
        bool runGenerators = true,
        CancellationToken cancellationToken = default)
    {
        var result = await _pipeline.AnalyzeAsync(packageId, version, assembly, cancellationToken: cancellationToken);
        if (result == null || result.CompilerArgsFile == null)
        {
            _console.MarkupLine("[red]✗[/] Cannot verify - no compiler arguments could be extracted from the package");
            return 1;
        }

        var complogPath = await CompLogFileCreator.CreateCompLogFileAsync(
            packageId,
            result.Package.Version,
            result.WorkingDirectory,
            result.WorkingDirectory,
            result.SelectedTfm,
            result.SelectedAssemblies,
            result.Ledger,
            runGenerators);

        if (!File.Exists(complogPath))
        {
            _console.MarkupLine("[red]✗[/] Cannot verify - complog creation failed");
            return 1;
        }

        // The pipeline analyzed exactly one assembly and put it first, and the complog describes
        // that one - a package shipping several (NUnit: nunit.framework + nunit.framework.legacy)
        // is captured one at a time, since a single working directory holds a single
        // compilation's arguments and sources. Match the call to the assembly by name anyway, so
        // this can never compare one assembly's bytes against another's compilation.
        var originalAssembly = result.SelectedAssemblies.First();
        var assemblyName = Path.GetFileNameWithoutExtension(originalAssembly);
        if (result.SelectedAssemblies.Count > 1)
        {
            _console.MarkupLine(
                $"  [dim]Package ships {result.SelectedAssemblies.Count} assemblies for this TFM; verifying " +
                $"{Path.GetFileName(originalAssembly)} (pass --assembly to verify another)[/]");
        }

        var rebuild = await _rebuilder.RebuildAsync(
            result, complogPath, assemblyName,
            Path.Combine(result.WorkingDirectory, "verify-export"),
            fetchCompiler, cancellationToken);

        var ledgerPath = Path.Combine(result.WorkingDirectory, $"{packageId}.{result.Package.Version}.reconstruction.json");
        await result.Ledger.SaveAsync(ledgerPath);

        if (!rebuild.Success)
        {
            _console.MarkupLine($"[red]✗[/] {rebuild.Failure}");
            return 1;
        }

        return Compare(originalAssembly, result.WorkingDirectory, result.SelectedTfm, rebuild);
    }

    private int Compare(string originalAssembly, string workingDirectory, string? tfm, RebuildOutcome rebuild)
    {
        _console.WriteLine();
        _console.MarkupLine("[yellow]Comparing rebuilt assembly against the package original[/]");

        var assemblyResult = BinaryDiffClassifier.CompareAssemblies(originalAssembly, rebuild.RebuiltAssembly!);
        var originalPdb = FindOriginalPdb(originalAssembly, workingDirectory, tfm);

        ComparisonResult? pdbResult = null;
        if (originalPdb != null && rebuild.RebuiltPdb != null && File.Exists(rebuild.RebuiltPdb))
        {
            pdbResult = BinaryDiffClassifier.ComparePdbs(originalPdb, rebuild.RebuiltPdb);
        }
        else
        {
            // /debug:embedded builds carry the PDB inside the assembly; extract both so the
            // PDB-level causes get explained instead of showing up as opaque byte ranges.
            var originalExtracted = Path.Combine(rebuild.ExportDir, "original.embedded.pdb");
            var rebuiltExtracted = Path.Combine(rebuild.ExportDir, "rebuilt.embedded.pdb");
            if (BinaryDiffClassifier.TryExtractEmbeddedPdb(originalAssembly, originalExtracted) &&
                BinaryDiffClassifier.TryExtractEmbeddedPdb(rebuild.RebuiltAssembly!, rebuiltExtracted))
            {
                pdbResult = BinaryDiffClassifier.ComparePdbs(originalExtracted, rebuiltExtracted);
            }
        }

        if (assemblyResult.ExactMatch)
        {
            _console.MarkupLine($"[green]✓ Assembly matches byte-for-byte[/] ({Path.GetFileName(originalAssembly)})");
            if (pdbResult is { ExactMatch: true })
            {
                _console.MarkupLine("[green]✓ PDB matches byte-for-byte[/]");
            }
            return 0;
        }

        if (assemblyResult.DerivedOnly)
        {
            _console.MarkupLine("[yellow]≈ Assembly content matches[/] - only derived fields differ:");
            foreach (var diff in assemblyResult.DerivedDifferences)
            {
                _console.MarkupLine($"  [dim]• {diff}[/]");
            }
            _console.MarkupLine("[dim]  Derived fields (MVID, timestamps, PDB id, signature) trail the PDB and signing key;[/]");
            _console.MarkupLine("[dim]  the causes below explain the remaining drift.[/]");
        }
        else
        {
            _console.MarkupLine("[red]✗ Assembly has real content differences:[/]");
            foreach (var diff in assemblyResult.RealDifferences.Take(10))
            {
                _console.MarkupLine($"  [dim]• {diff}[/]");
            }
        }

        if (pdbResult != null && !pdbResult.ExactMatch)
        {
            // Raw byte clusters say nothing actionable about a PDB, so only the explained
            // findings are listed - but say so rather than printing an empty section, which
            // reads as "the PDB matched".
            var findings = pdbResult.RealDifferences.Where(f => !f.StartsWith("bytes differ")).Take(10).ToList();
            if (findings.Count > 0)
            {
                _console.MarkupLine("[yellow]PDB differences:[/]");
                foreach (var finding in findings)
                {
                    _console.MarkupLine($"  [dim]• {finding}[/]");
                }
            }
            else
            {
                _console.MarkupLine("[yellow]PDB differs[/] [dim]- no attributable cause found[/]");
            }
        }

        return assemblyResult.DerivedOnly ? 2 : 1;
    }

    private string? FindOriginalPdb(string assemblyPath, string workingDirectory, string? tfm)
    {
        var pdbName = Path.GetFileNameWithoutExtension(assemblyPath) + ".pdb";

        var next = Path.Combine(Path.GetDirectoryName(assemblyPath)!, pdbName);
        if (File.Exists(next))
        {
            return next;
        }

        var symbolsDir = Path.Combine(workingDirectory, "symbols");
        if (!Directory.Exists(symbolsDir))
        {
            return null;
        }

        var candidates = Directory.GetFiles(symbolsDir, pdbName, SearchOption.AllDirectories);
        return candidates.FirstOrDefault(c => tfm != null && c.Contains($"{Path.DirectorySeparatorChar}{tfm}{Path.DirectorySeparatorChar}"))
               ?? candidates.FirstOrDefault();
    }
}
