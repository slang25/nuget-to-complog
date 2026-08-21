using NuGetToCompLog.Abstractions;
using NuGetToCompLog.Domain;
using NuGetToCompLog.Services.NuGet;
using NuGetToCompLog.Services.Pdb;
using NuGetToCompLog.Infrastructure.SourceDownload;
using NuGetToCompLog.Services.Reconstruction;

namespace NuGetToCompLog.Services;

/// <summary>
/// Reusable pipeline that downloads, extracts, and analyzes a NuGet package.
/// Produces a <see cref="PackageExtractionResult"/> that can be consumed by
/// different commands (complog creation, eject, etc.).
/// </summary>
public class PackageAnalysisPipeline
{
    private readonly INuGetClient _nugetClient;
    private readonly PackageExtractionService _extractionService;
    private readonly ITargetFrameworkSelector _tfmSelector;
    private readonly PdbDiscoveryService _pdbDiscovery;
    private readonly IPdbReader _pdbReader;
    private readonly SymbolServerClient _symbolServerClient;
    private readonly ISourceFileDownloader _sourceDownloader;
    private readonly IFileSystemService _fileSystem;
    private readonly IConsoleWriter _console;

    public PackageAnalysisPipeline(
        INuGetClient nugetClient,
        PackageExtractionService extractionService,
        ITargetFrameworkSelector tfmSelector,
        PdbDiscoveryService pdbDiscovery,
        IPdbReader pdbReader,
        SymbolServerClient symbolServerClient,
        ISourceFileDownloader sourceDownloader,
        IFileSystemService fileSystem,
        IConsoleWriter console)
    {
        _nugetClient = nugetClient;
        _extractionService = extractionService;
        _tfmSelector = tfmSelector;
        _pdbDiscovery = pdbDiscovery;
        _pdbReader = pdbReader;
        _symbolServerClient = symbolServerClient;
        _sourceDownloader = sourceDownloader;
        _fileSystem = fileSystem;
        _console = console;
    }

    /// <summary>
    /// Analyzes a NuGet package: downloads, extracts, reads PDB metadata,
    /// downloads source files, and returns a result capturing all artifacts.
    ///
    /// The working directory describes exactly one compilation - one compiler-arguments.txt,
    /// one metadata-references.json, one source manifest, one sources/ tree - so when a package
    /// ships several assemblies for the chosen TFM only one of them is analyzed, and
    /// <paramref name="assemblyName"/> says which.
    /// </summary>
    public async Task<PackageExtractionResult?> AnalyzeAsync(
        string packageId,
        string? version,
        string? assemblyName = null,
        CancellationToken cancellationToken = default)
    {
        var ledger = new ReconstructionLedger();
        var workingDirectory = _fileSystem.CreateTempDirectory();
        _console.MarkupLine($"[dim]Working directory: {workingDirectory}[/]");
        _console.WriteLine();

        if (version == null)
        {
            version = await _nugetClient.GetLatestVersionAsync(packageId, cancellationToken);
            _console.MarkupLine($"[dim]Latest version: {version}[/]");
        }

        var package = new PackageIdentity(packageId, version);

        // Download package
        string packagePath = "";
        await _console.ExecuteWithStatusAsync("Downloading package...", async () =>
        {
            packagePath = await _nugetClient.DownloadPackageAsync(package, workingDirectory, cancellationToken);
        });
        _console.MarkupLine($"[green]\u2713[/] Downloaded package to: [dim]{Path.GetFileName(packagePath)}[/]");
        _console.WriteLine();

        // Extract package
        var extractPath = Path.Combine(workingDirectory, "extracted");
        await _extractionService.ExtractPackageAsync(packagePath, extractPath);
        _console.MarkupLine("[green]\u2713[/] Extracted package");
        _console.WriteLine();

        // Find and select assemblies
        var allAssemblies = _extractionService.FindAssemblies(extractPath);
        DisplayAssembliesTree(allAssemblies, extractPath);

        var (selectedAssemblies, selectedTfm) = _tfmSelector.SelectBestTargetFramework(allAssemblies, extractPath);
        if (selectedTfm != null)
        {
            _console.MarkupLine($"[green]\u2713[/] Selected best TFM: [cyan]{selectedTfm}[/] with [yellow]{selectedAssemblies.Count}[/] assemblies");
            _console.WriteLine();
        }

        // One working directory describes one compilation, so pick the assembly it will describe
        // and put it first - everything downstream (the complog's single compiler call, the
        // verify comparison) reads selectedAssemblies[0] and would otherwise risk pairing one
        // assembly's bytes with another's compiler arguments.
        var primaryAssembly = SelectPrimaryAssembly(selectedAssemblies, packageId, assemblyName);
        if (primaryAssembly == null && selectedAssemblies.Count > 0)
        {
            _console.MarkupLine(
                $"[red]\u2717[/] No assembly named [cyan]{assemblyName}[/] in this package" +
                (selectedTfm != null ? $" for [cyan]{selectedTfm}[/]" : "") + ". Available: " +
                string.Join(", ", selectedAssemblies.Select(Path.GetFileName)));
            return null;
        }
        selectedAssemblies = selectedAssemblies
            .OrderByDescending(a => a == primaryAssembly)
            .ToList();
        if (primaryAssembly != null && selectedAssemblies.Count > 1)
        {
            _console.MarkupLine(
                $"[yellow]\u26a0[/] This TFM ships {selectedAssemblies.Count} assemblies; only " +
                $"[cyan]{Path.GetFileName(primaryAssembly)}[/] is analyzed - the complog describes it alone");
            _console.MarkupLine(
                "   [dim]Pass --assembly <name.dll> to capture one of the others: " +
                string.Join(", ", selectedAssemblies.Skip(1).Select(a => Path.GetFileName(a))) + "[/]");
            _console.WriteLine();
        }

        ledger.Describe(
            $"{package.Id}/{package.Version}", selectedTfm,
            primaryAssembly != null ? Path.GetFileName(primaryAssembly) : null);

        // Handle PDB discovery and symbols download
        var hasEmbeddedPdb = primaryAssembly != null && _pdbDiscovery.HasEmbeddedPdb(primaryAssembly);
        if (!hasEmbeddedPdb)
        {
            await TryDownloadSymbolsAsync(package, workingDirectory, cancellationToken);
        }
        else
        {
            _console.MarkupLine("[dim]\u26a0 Skipping symbols package download - the selected assembly has an embedded PDB[/]");
            _console.WriteLine();
        }

        if (primaryAssembly != null)
        {
            _console.MarkupLine(
                $"  [cyan]\u2192[/] Processing [dim]{Path.GetFileName(primaryAssembly)}[/] from TFM: [yellow]{selectedTfm}[/]");
            await ProcessAssemblyAsync(primaryAssembly, workingDirectory, ledger, cancellationToken);
            _console.WriteLine();
        }

        var compilerArgsFile = Path.Combine(workingDirectory, "compiler-arguments.txt");
        var metadataRefsFile = Path.Combine(workingDirectory, "metadata-references.txt");
        var sourcesDir = Path.Combine(workingDirectory, "sources");
        var resourcesDir = Path.Combine(workingDirectory, "resources");

        return new PackageExtractionResult(
            Package: package,
            WorkingDirectory: workingDirectory,
            ExtractPath: extractPath,
            SelectedTfm: selectedTfm,
            SelectedAssemblies: selectedAssemblies,
            CompilerArgsFile: File.Exists(compilerArgsFile) ? compilerArgsFile : null,
            MetadataRefsFile: File.Exists(metadataRefsFile) ? metadataRefsFile : null,
            SourcesDirectory: sourcesDir,
            ResourcesDirectory: Directory.Exists(resourcesDir) ? resourcesDir : null,
            Ledger: ledger);
    }

    /// <summary>
    /// The one assembly this run captures. Defaults to the one named after the package (NUnit's
    /// nunit.framework.dll sits beside nunit.framework.legacy.dll, and only one of them is what
    /// the package is), falling back to the first. A caller-supplied name wins, matched with or
    /// without the .dll suffix; null means the caller asked for an assembly the package has not
    /// got, or there are no assemblies at all.
    /// </summary>
    private static string? SelectPrimaryAssembly(List<string> assemblies, string packageId, string? assemblyName)
    {
        if (assemblyName != null)
        {
            var wanted = Path.GetFileNameWithoutExtension(assemblyName);
            return assemblies.FirstOrDefault(a => string.Equals(
                Path.GetFileNameWithoutExtension(a), wanted, StringComparison.OrdinalIgnoreCase));
        }

        return assemblies.FirstOrDefault(a => string.Equals(
                   Path.GetFileNameWithoutExtension(a), packageId, StringComparison.OrdinalIgnoreCase))
               ?? assemblies.FirstOrDefault();
    }

    private void DisplayAssembliesTree(List<string> assemblies, string extractPath)
    {
        var nodes = new Dictionary<string, List<string>>();

        foreach (var assembly in assemblies)
        {
            var relativePath = Path.GetRelativePath(extractPath, assembly);
            var parts = relativePath.Split(Path.DirectorySeparatorChar);
            var framework = parts.Length > 1 ? parts[1] : "unknown";

            if (!nodes.ContainsKey($"[cyan]{framework}[/]"))
            {
                nodes[$"[cyan]{framework}[/]"] = new List<string>();
            }
            nodes[$"[cyan]{framework}[/]"].Add($"[yellow]{Path.GetFileName(assembly)}[/]");
        }

        _console.WriteTree($"[green]Found {assemblies.Count} assemblies across all TFMs[/]", nodes);
        _console.WriteLine();
    }

    private async Task TryDownloadSymbolsAsync(PackageIdentity package, string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            string? snupkgPath = null;
            await _console.ExecuteWithStatusAsync("Attempting to download symbols package (.snupkg)...", async () =>
            {
                snupkgPath = await _nugetClient.DownloadSymbolsPackageAsync(package, workingDirectory, cancellationToken);
            });

            if (snupkgPath != null)
            {
                var symbolsExtractPath = Path.Combine(workingDirectory, "symbols");
                await _extractionService.ExtractPackageAsync(snupkgPath, symbolsExtractPath);

                var pdbs = _extractionService.FindPdbFiles(symbolsExtractPath);
                if (pdbs.Count > 0)
                {
                    var nodes = new Dictionary<string, List<string>>();
                    nodes["PDB Files"] = pdbs.Select(p => $"[blue]{Path.GetRelativePath(symbolsExtractPath, p)}[/]").ToList();
                    _console.WriteTree($"[green]\u2713 Downloaded symbols package with {pdbs.Count} PDB file(s)[/]", nodes);
                }
                else
                {
                    _console.MarkupLine("[green]\u2713[/] Downloaded symbols package (no PDB files found inside)");
                }
                _console.WriteLine();
            }
            else
            {
                _console.MarkupLine("[yellow]\u26a0[/] Symbols package (.snupkg) not available for this package");
                _console.MarkupLine("   [dim]Note: Not all packages publish symbol packages to NuGet.org[/]");
                _console.WriteLine();
            }
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[yellow]\u26a0[/] Could not download symbols package: [dim]{ex.Message}[/]");
            _console.WriteLine();
        }
    }

    private async Task ProcessAssemblyAsync(
        string assemblyPath, string workingDirectory, ReconstructionLedger ledger, CancellationToken cancellationToken)
    {
        var hasEmbeddedPdb = _pdbDiscovery.HasEmbeddedPdb(assemblyPath);
        var hasReproducibleMarker = _pdbDiscovery.HasReproducibleMarker(assemblyPath);

        if (hasEmbeddedPdb)
        {
            _console.MarkupLine("  [green]\u2713 Found embedded PDB[/]");
            if (hasReproducibleMarker)
            {
                _console.MarkupLine("  [green]\u2713 Found reproducible/deterministic marker[/]");
            }

            ledger.Recorded(ReconstructionLedger.CategorySymbols, "embedded PDB",
                "shipped inside the assembly itself, so it is unambiguously the one for these bytes");
            var metadata = await _pdbReader.ExtractMetadataAsync(assemblyPath, null, hasReproducibleMarker, cancellationToken);
            using var pdbHandle = GetPdbMetadataReader(assemblyPath, null);
            await SaveMetadataAsync(metadata, assemblyPath, pdbHandle?.Reader, workingDirectory, ledger);
            return;
        }

        var pdbPath = await _pdbReader.FindPdbAsync(assemblyPath, workingDirectory);

        if (pdbPath == null)
        {
            // Last resort: query public symbol servers (SSQP). Recovers Microsoft.* /
            // System.* / Azure.* packages that ship symbols to a symbol server instead
            // of a .snupkg on nuget.org. The PDB lands next to the assembly, so we
            // re-run discovery to locate it through the normal path.
            var symbolServerPdb = await _symbolServerClient.TryDownloadPdbAsync(assemblyPath, cancellationToken);
            if (symbolServerPdb != null)
            {
                pdbPath = await _pdbReader.FindPdbAsync(assemblyPath, workingDirectory);
            }
        }

        if (pdbPath != null)
        {
            _console.MarkupLine($"  [green]\u2713 Found external PDB:[/] [cyan]{Path.GetFileName(pdbPath)}[/]");
            ledger.Recorded(ReconstructionLedger.CategorySymbols, Path.GetFileName(pdbPath),
                "external PDB from the symbol package or a symbol server");
            var metadata = await _pdbReader.ExtractMetadataAsync(assemblyPath, pdbPath, hasReproducibleMarker, cancellationToken);
            using var pdbHandle = GetPdbMetadataReader(assemblyPath, pdbPath);
            await SaveMetadataAsync(metadata, assemblyPath, pdbHandle?.Reader, workingDirectory, ledger);
        }
        else
        {
            ledger.Missing(ReconstructionLedger.CategorySymbols, Path.GetFileName(assemblyPath),
                "no PDB found - the package ships neither embedded symbols nor a .snupkg, and no " +
                "symbol server had one, so nothing about the compilation could be read");
            _console.WritePanel(
                "\u26a0 Missing Symbols",
                "[yellow]No PDB found[/] - cannot extract compiler arguments\n\n" +
                "[dim]Note: Reproducible builds with embedded symbols are required for complog extraction[/]",
                "Yellow");
        }
    }

    private sealed class PdbReaderHandle : IDisposable
    {
        private readonly System.Reflection.Metadata.MetadataReaderProvider _provider;
        public System.Reflection.Metadata.MetadataReader Reader { get; }

        public PdbReaderHandle(System.Reflection.Metadata.MetadataReaderProvider provider)
        {
            _provider = provider;
            Reader = provider.GetMetadataReader();
        }

        public void Dispose() => _provider.Dispose();
    }

    private PdbReaderHandle? GetPdbMetadataReader(string assemblyPath, string? pdbPath)
    {
        try
        {
            if (pdbPath == null)
            {
                byte[] pdbBytes;

                using (var peStream = File.OpenRead(assemblyPath))
                using (var peReader = new System.Reflection.PortableExecutable.PEReader(peStream))
                {
                    var embeddedPdb = peReader.ReadDebugDirectory()
                        .FirstOrDefault(d => d.Type == System.Reflection.PortableExecutable.DebugDirectoryEntryType.EmbeddedPortablePdb);
                    if (embeddedPdb.Type != System.Reflection.PortableExecutable.DebugDirectoryEntryType.EmbeddedPortablePdb)
                    {
                        return null;
                    }

                    var tempProvider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(embeddedPdb);
                    var pdbSize = embeddedPdb.DataSize;
                    pdbBytes = new byte[pdbSize];
                    var section = peReader.GetSectionData(embeddedPdb.DataRelativeVirtualAddress);
                    var span = section.GetContent(0, pdbSize);
                    span.CopyTo(pdbBytes);
                    tempProvider.Dispose();
                }

                var immutableBytes = System.Collections.Immutable.ImmutableArray.Create(pdbBytes);
                var provider = System.Reflection.Metadata.MetadataReaderProvider.FromPortablePdbImage(immutableBytes);
                return new PdbReaderHandle(provider);
            }
            else
            {
                var pdbBytes = File.ReadAllBytes(pdbPath);
                var immutableBytes = System.Collections.Immutable.ImmutableArray.Create(pdbBytes);
                var provider = System.Reflection.Metadata.MetadataReaderProvider.FromPortablePdbImage(immutableBytes);
                return new PdbReaderHandle(provider);
            }
        }
        catch
        {
            return null;
        }
    }

    private async Task SaveMetadataAsync(
        PdbMetadata metadata,
        string assemblyPath,
        System.Reflection.Metadata.MetadataReader? pdbMetadataReader,
        string workingDirectory,
        ReconstructionLedger ledger)
    {
        if (metadata.CompilerArguments.Count > 0)
        {
            var compilerArgsPath = Path.Combine(workingDirectory, "compiler-arguments.txt");
            await _fileSystem.WriteAllLinesAsync(compilerArgsPath, metadata.CompilerArguments);
            _console.MarkupLine($"  [green]\u2713[/] Saved {metadata.CompilerArguments.Count} compiler arguments");
        }

        if (metadata.MetadataReferences.Count > 0)
        {
            var referencesPath = Path.Combine(workingDirectory, "metadata-references.txt");
            await _fileSystem.WriteAllLinesAsync(referencesPath, metadata.MetadataReferences.Select(r => r.FileName));

            // Full-fidelity records (MVID, timestamp, size) so reference acquisition can verify
            // it found the exact assemblies the original compiler used, not just same-named ones.
            var referencesJsonPath = Path.Combine(workingDirectory, "metadata-references.json");
            await File.WriteAllTextAsync(referencesJsonPath,
                System.Text.Json.JsonSerializer.Serialize(metadata.MetadataReferences,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            _console.MarkupLine($"  [green]\u2713[/] Saved {metadata.MetadataReferences.Count} metadata references");
        }

        if (!string.IsNullOrEmpty(metadata.SourceLinkJson))
        {
            var sourceLinkPath = Path.Combine(workingDirectory, "source-link.json");
            await _fileSystem.WriteAllTextAsync(sourceLinkPath, metadata.SourceLinkJson);
        }

        if (metadata.EmbeddedResources.Count > 0)
        {
            var resourcesDir = Path.Combine(workingDirectory, "resources");
            _fileSystem.CreateDirectory(resourcesDir);

            var resourceMappings = new List<string>();

            foreach (var resource in metadata.EmbeddedResources)
            {
                var fileName = resource.Name.Replace("/", "_").Replace("\\", "_");
                var filePath = Path.Combine(resourcesDir, fileName);
                await _fileSystem.WriteAllBytesAsync(filePath, resource.Content);

                resourceMappings.Add($"{fileName}|{resource.Name}");
            }

            var mappingPath = Path.Combine(workingDirectory, "resource-mappings.txt");
            await _fileSystem.WriteAllLinesAsync(mappingPath, resourceMappings);

            _console.MarkupLine($"  [green]\u2713[/] Saved {metadata.EmbeddedResources.Count} embedded resource(s)");
        }

        if (metadata.SourceFiles.Count > 0)
        {
            var sourcesDir = Path.Combine(workingDirectory, "sources");
            _fileSystem.CreateDirectory(sourcesDir);

            // Derive the original project root so every document keeps its structure relative to
            // it \u2014 including compiler-generated files under obj/ \u2014 and assign each document its
            // local path up front. Document order is preserved from the PDB Documents table.
            var debugConfig = DebugConfigurationExtractor.ExtractDebugConfiguration(assemblyPath);
            var mapper = SourcePathMapper.Create(metadata.SourceFiles.Select(sf => sf.Path), debugConfig.PdbPath);
            var sourceFiles = metadata.SourceFiles
                .Select(sf => sf with { LocalPath = mapper.MapToLocal(sf.Path) })
                .ToList();

            var embeddedCount = 0;
            foreach (var sourceFile in sourceFiles.Where(sf => sf.HasContent))
            {
                var filePath = Path.Combine(sourcesDir, sourceFile.LocalPath!);
                var directory = Path.GetDirectoryName(filePath);
                if (directory != null)
                {
                    _fileSystem.CreateDirectory(directory);
                }

                // Write raw bytes: the embedded blob preserves BOM and line endings exactly,
                // and the document hash must keep matching.
                if (sourceFile.ContentBytes != null)
                {
                    await _fileSystem.WriteAllBytesAsync(filePath, sourceFile.ContentBytes);
                }
                else
                {
                    await _fileSystem.WriteAllTextAsync(filePath, sourceFile.Content!);
                }
                embeddedCount++;
            }

            if (embeddedCount > 0)
            {
                _console.MarkupLine($"  [green]\u2713[/] Saved {embeddedCount} embedded source files");
            }

            if (!string.IsNullOrEmpty(metadata.SourceLinkJson))
            {
                if (_sourceDownloader is HttpSourceFileDownloader httpDownloader)
                {
                    var downloadedCount = await httpDownloader.DownloadSourceFilesAsync(
                        sourceFiles,
                        metadata.SourceLinkJson,
                        sourcesDir,
                        assemblyPath,
                        pdbMetadataReader);

                    if (downloadedCount > 0)
                    {
                        _console.MarkupLine($"  [green]\u2713[/] Downloaded {downloadedCount} source files from Source Link");
                    }
                }
                else
                {
                    var downloadedCount = await _sourceDownloader.DownloadSourceFilesAsync(
                        sourceFiles,
                        metadata.SourceLinkJson,
                        sourcesDir);

                    if (downloadedCount > 0)
                    {
                        _console.MarkupLine($"  [green]\u2713[/] Downloaded {downloadedCount} source files from Source Link");
                    }
                }
            }

            var verification = VerifySourceHashes(sourceFiles, sourcesDir);
            RecordSourceEvidence(sourceFiles, sourcesDir, verification, ledger);
            if (mapper.RootPrefix != null)
            {
                ledger.Derived(ReconstructionLedger.CategoryOption, "/pathmap",
                    $"project root '{mapper.RootPrefix}' derived from the recorded document paths");
            }

            var manifest = new SourceManifest(
                sourceFiles.Select(sf => new SourceManifestEntry(
                    sf.Path,
                    sf.LocalPath!,
                    sf.HashAlgorithm,
                    sf.Hash != null ? Convert.ToHexStringLower(sf.Hash) : null,
                    sf.IsEmbedded)).ToList(),
                mapper.RootPrefix);
            await manifest.SaveAsync(workingDirectory);
        }
    }

    /// <summary>
    /// Verifies every on-disk source against the checksum in the PDB Documents table, repairing
    /// line-ending/BOM differences (Source Link serves committed bytes, but the original build may
    /// have compiled a CRLF checkout). Files that still mismatch will produce a different PDB \u2014
    /// and through the PdbChecksum debug entry a different assembly \u2014 so they are called out.
    /// </summary>
    private Dictionary<string, SourceHashVerification> VerifySourceHashes(
        List<SourceFileInfo> sourceFiles, string sourcesDir)
    {
        var results = new Dictionary<string, SourceHashVerification>(StringComparer.Ordinal);
        var fixedCount = 0;
        var mismatched = new List<string>();

        foreach (var sourceFile in sourceFiles)
        {
            var filePath = Path.Combine(sourcesDir, sourceFile.LocalPath!);
            if (!File.Exists(filePath))
            {
                continue;
            }

            var result = LineEndingNormalizer.VerifyAndFix(filePath, sourceFile.Hash, sourceFile.HashAlgorithm);
            results[sourceFile.Path] = result;
            if (result == SourceHashVerification.Fixed)
            {
                fixedCount++;
            }
            else if (result == SourceHashVerification.Mismatch)
            {
                mismatched.Add(sourceFile.Path);
            }
        }

        if (fixedCount > 0)
        {
            _console.MarkupLine($"  [green]\u2713[/] Fixed line endings on {fixedCount} source file(s) to match PDB checksums");
        }

        foreach (var path in mismatched.Take(5))
        {
            _console.MarkupLine($"  [yellow]\u26a0[/] Source content does not match PDB checksum: [dim]{path}[/]");
        }
        if (mismatched.Count > 5)
        {
            _console.MarkupLine($"  [yellow]\u26a0[/] ... and {mismatched.Count - 5} more source checksum mismatches");
        }

        return results;
    }

    /// <summary>
    /// Records how each source document was obtained, judged by evidence rather than by which
    /// code path produced it: bytes that hash to the checksum in the PDB Documents table *are*
    /// the original input, whether they came from the embedded blob or from Source Link. Bytes
    /// that do not are a substitution (decompiled output, or a different revision of the file),
    /// and that is what makes a byte-for-byte rebuild impossible rather than merely unproven.
    /// </summary>
    private static void RecordSourceEvidence(
        List<SourceFileInfo> sourceFiles,
        string sourcesDir,
        Dictionary<string, SourceHashVerification> verification,
        ReconstructionLedger ledger)
    {
        var embedded = 0;
        var downloaded = 0;
        var problems = new List<(InputEvidence Evidence, string Name, string Detail)>();

        foreach (var sourceFile in sourceFiles)
        {
            var name = sourceFile.LocalPath ?? sourceFile.Path;
            if (!File.Exists(Path.Combine(sourcesDir, sourceFile.LocalPath!)))
            {
                problems.Add((InputEvidence.Missing, name,
                    "neither embedded in the PDB nor available from Source Link"));
                continue;
            }

            switch (verification.GetValueOrDefault(sourceFile.Path))
            {
                case SourceHashVerification.Match:
                case SourceHashVerification.Fixed:
                    if (sourceFile.IsEmbedded)
                    {
                        embedded++;
                    }
                    else
                    {
                        downloaded++;
                    }
                    break;
                case SourceHashVerification.Mismatch:
                    problems.Add((InputEvidence.Substituted, name,
                        "content does not hash to the recorded checksum - decompiled, or a different revision"));
                    break;
                default:
                    problems.Add((InputEvidence.Assumed, name,
                        "the PDB recorded no checksum for this document, so the content cannot be verified"));
                    break;
            }
        }

        ledger.Recorded(ReconstructionLedger.CategorySource, "embedded in PDB",
            "extracted from the PDB and hashed to the recorded checksum", embedded);
        ledger.Recorded(ReconstructionLedger.CategorySource, "from Source Link",
            "downloaded and hashed to the recorded checksum", downloaded);

        foreach (var (evidence, name, detail) in problems.Take(MaxIndividualEntries))
        {
            ledger.Add(ReconstructionLedger.CategorySource, name, evidence, detail);
        }
        foreach (var group in problems.Skip(MaxIndividualEntries).GroupBy(p => p.Evidence))
        {
            ledger.Add(ReconstructionLedger.CategorySource, $"{group.Count()} further document(s)",
                group.Key, group.First().Detail, group.Count());
        }
    }

    /// <summary>
    /// How many individually-named problem entries the ledger carries before rolling the rest
    /// up. A package recovered entirely by decompilation would otherwise list every document.
    /// </summary>
    private const int MaxIndividualEntries = 20;
}
