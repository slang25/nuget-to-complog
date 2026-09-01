using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NuGetToCompLog.Services.SourceBuild;

/// <summary>
/// How a source-built assembly compares to the binary the package shipped.
/// </summary>
public enum BinaryEquivalence
{
    /// <summary>Byte-for-byte identical to the shipped assembly.</summary>
    Identical,

    /// <summary>
    /// Every content byte matches; only fields derived from the signing key and the PDB differ
    /// (MVID, timestamps, PDB id, Authenticode signature). This is the best result achievable for
    /// a signed package, because the signature covers bytes we cannot reproduce without the
    /// publisher's key - so it is the realistic pass, not a near-miss.
    /// </summary>
    ContentEquivalent,

    /// <summary>
    /// Built with a compiler or runtime other than the one the package used, so the bytes are not
    /// comparable - but every publicly visible type, member and signature matches the shipped
    /// assembly, as does every referenced assembly identity. This is what building a library from
    /// source on your own machine normally gets you, and it is the level a source build reaches
    /// whenever the original toolchain is no longer installable.
    /// </summary>
    ApiEquivalent,

    /// <summary>
    /// The rebuild is not the same library: it differs from the shipped assembly by more than
    /// derived fields under the exact toolchain, or its public surface does not match.
    /// </summary>
    Divergent,
}

/// <summary>
/// What a cached assembly is and how far it can be trusted. Written next to the assembly so the
/// claim travels with the artifact: anyone auditing the build output can see which package it
/// stands in for, which compiler produced it, and how it compared to the published binary.
/// </summary>
public record SourceBuildProvenance
{
    public required string PackageId { get; init; }
    public required string PackageVersion { get; init; }
    public required string TargetFramework { get; init; }
    public required string AssemblyFileName { get; init; }
    public required string Sha256 { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required BinaryEquivalence Equivalence { get; init; }

    /// <summary>The derived-field differences accepted for <see cref="BinaryEquivalence.ContentEquivalent"/>.</summary>
    public required IReadOnlyList<string> AcceptedDifferences { get; init; }

    public required string? Compiler { get; init; }
    public required bool CompilerWasExact { get; init; }
    public required bool RuntimeWasExact { get; init; }

    /// <summary>
    /// How many public types and members were compared, when equivalence rested on the surface
    /// rather than on the bytes. Null when the byte comparison settled it.
    /// </summary>
    public int? SurfaceMembersChecked { get; init; }

    /// <summary>The reconstruction ledger's verdict: exact, unconfirmed, or impossible.</summary>
    public required string ReconstructionOutlook { get; init; }

    public required string ComplogSha256 { get; init; }
}

/// <summary>
/// The machine-wide store of assemblies built from a package's own source.
///
/// Keyed by package identity and the lib folder's target framework, because that triple is what a
/// consuming build resolves - the same package can ship a different compilation per TFM, and
/// substituting one for another would silently change which APIs exist. The cache holds the
/// assembly, its PDB, the complog it was built from and the provenance record, so a build that
/// consumes it can be re-derived and audited later without going back to nuget.org.
///
/// One triple can hold several assemblies: a package that ships nunit.framework.dll beside
/// nunit.framework.legacy.dll resolves both out of the same lib folder, and a build that asked for
/// the package expects both to be substituted. So the assembly, its PDB and its provenance are
/// per-assembly - the provenance file is named after the assembly it describes - while the complog
/// and the ledger, which cover the package's whole extraction, are shared by the entry.
/// </summary>
public class SourceBuildCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _root;

    public SourceBuildCache(string? root = null)
    {
        _root = root ?? DefaultRoot();
    }

    /// <summary>
    /// The cache root. Matches the location the generated MSBuild targets probe, and can be
    /// redirected with NUGET_TO_COMPLOG_CACHE so CI can put it on a restorable path.
    /// </summary>
    public static string DefaultRoot() =>
        Environment.GetEnvironmentVariable("NUGET_TO_COMPLOG_CACHE")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "nuget-to-complog",
            "source-built");

    public string Root => _root;

    public string DirectoryFor(string packageId, string version, string targetFramework) =>
        Path.Combine(_root, packageId, version, targetFramework);

    public string AssemblyPath(string packageId, string version, string targetFramework, string assemblyFileName) =>
        Path.Combine(DirectoryFor(packageId, version, targetFramework), assemblyFileName);

    /// <summary>
    /// The provenance record for one cached assembly. Named after the assembly rather than the
    /// entry, so a second assembly built for the same triple adds a record instead of replacing
    /// the first one's. <see cref="CachedAssembly"/> in the MSBuild task derives the same name.
    /// </summary>
    public string ProvenancePath(string packageId, string version, string targetFramework, string assemblyFileName) =>
        Path.Combine(DirectoryFor(packageId, version, targetFramework), $"{assemblyFileName}.provenance.json");

    /// <summary>
    /// True when the cache holds an intact assembly for this triple, checked against the hash its
    /// own provenance recorded so a truncated or edited file is treated as a miss.
    ///
    /// Deliberately not checked against the lock file's hash. A machine with a different SDK
    /// produces different bytes from the same source, and that is an accepted outcome now, so
    /// insisting on the recorded hash here would mean never reusing the cache on such a machine.
    /// Whether the rebuild is the same library is settled by the equivalence gate, not by this.
    /// </summary>
    public bool Contains(string packageId, string version, string targetFramework, string assemblyFileName)
    {
        var path = AssemblyPath(packageId, version, targetFramework, assemblyFileName);
        if (!File.Exists(path))
        {
            return false;
        }

        var provenance = TryReadProvenance(packageId, version, targetFramework, assemblyFileName);
        return provenance != null &&
               string.Equals(provenance.AssemblyFileName, assemblyFileName, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Sha256(path), provenance.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Stores a rebuilt assembly with everything needed to justify it later, and returns the
    /// provenance record. Replaces whatever was cached for this assembly - a previous,
    /// differently-classified build of it leaves nothing behind - while any other assembly cached
    /// for the same triple keeps its own files and its own record.
    /// </summary>
    public async Task<SourceBuildProvenance> StoreAsync(
        string packageId,
        string version,
        string targetFramework,
        string rebuiltAssembly,
        string? rebuiltPdb,
        string complogPath,
        string? ledgerPath,
        BinaryEquivalence equivalence,
        IReadOnlyList<string> acceptedDifferences,
        string? compiler,
        bool compilerWasExact,
        bool runtimeWasExact,
        string reconstructionOutlook,
        int? surfaceMembersChecked = null)
    {
        var directory = DirectoryFor(packageId, version, targetFramework);
        Directory.CreateDirectory(directory);

        var assemblyFileName = Path.GetFileName(rebuiltAssembly);
        var destination = Path.Combine(directory, assemblyFileName);

        // Clear this assembly's own record first: until the new one is written, a reader that
        // finds the file must not find a hash that once matched it.
        var provenancePath = ProvenancePath(packageId, version, targetFramework, assemblyFileName);
        File.Delete(provenancePath);

        File.Copy(rebuiltAssembly, destination, overwrite: true);

        if (rebuiltPdb != null && File.Exists(rebuiltPdb))
        {
            File.Copy(rebuiltPdb, Path.Combine(directory, Path.GetFileName(rebuiltPdb)), overwrite: true);
        }

        var cachedComplog = Path.Combine(directory, Path.GetFileName(complogPath));
        File.Copy(complogPath, cachedComplog, overwrite: true);

        if (ledgerPath != null && File.Exists(ledgerPath))
        {
            File.Copy(ledgerPath, Path.Combine(directory, Path.GetFileName(ledgerPath)), overwrite: true);
        }

        var provenance = new SourceBuildProvenance
        {
            PackageId = packageId,
            PackageVersion = version,
            TargetFramework = targetFramework,
            AssemblyFileName = assemblyFileName,
            Sha256 = Sha256(destination),
            Equivalence = equivalence,
            AcceptedDifferences = acceptedDifferences,
            Compiler = compiler,
            CompilerWasExact = compilerWasExact,
            RuntimeWasExact = runtimeWasExact,
            SurfaceMembersChecked = surfaceMembersChecked,
            ReconstructionOutlook = reconstructionOutlook,
            ComplogSha256 = Sha256(cachedComplog),
        };

        await File.WriteAllTextAsync(provenancePath, JsonSerializer.Serialize(provenance, JsonOptions));

        return provenance;
    }

    public SourceBuildProvenance? TryReadProvenance(
        string packageId, string version, string targetFramework, string assemblyFileName)
    {
        var path = ProvenancePath(packageId, version, targetFramework, assemblyFileName);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<SourceBuildProvenance>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Takes an exclusive lock on one cache entry, waiting for whoever holds it.
    ///
    /// A solution builds its projects in parallel, and a cold cache means several of them ask for
    /// the same assembly at the same moment. Storing an entry clears its directory first, so
    /// without this one process would be deleting the file another had just decided to link
    /// against. The lock is a file rather than a named mutex because named mutexes are
    /// process-local on Unix.
    /// </summary>
    public async Task<IDisposable> LockAsync(
        string packageId, string version, string targetFramework, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, $".{packageId}.{version}.{targetFramework}.lock".ToLowerInvariant());

        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                await Task.Delay(200, cancellationToken);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(200, cancellationToken);
            }
        }
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
