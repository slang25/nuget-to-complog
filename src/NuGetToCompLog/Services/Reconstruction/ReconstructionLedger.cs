using System.Text.Json;
using System.Text.Json.Serialization;
using NuGetToCompLog.Abstractions;

namespace NuGetToCompLog.Services.Reconstruction;

/// <summary>
/// How much evidence stands behind one input of the reconstructed compilation.
///
/// A NuGet package is not a self-describing build: the PDB records the compiler version, the
/// options blob, the references and most sources, but nothing records the analyzer set, the
/// generator package versions (they are PrivateAssets, so the nuspec never sees them) or
/// flags like /features: and /nowarn:. Every one of those gaps is filled by inference, and
/// this says which - so the output can be trusted for what it is rather than assumed to be a
/// faithful replay.
/// </summary>
public enum InputEvidence
{
    /// <summary>Read straight out of the package or its PDB, and verified where verifiable.</summary>
    Recorded,

    /// <summary>Computed deterministically from recorded data (pathmap roots, debug settings).</summary>
    Derived,

    /// <summary>Recovered from indirect evidence in the shipped assembly - never stated, but implied.</summary>
    Inferred,

    /// <summary>Guessed from candidates, then confirmed against recorded data (MVID, checksum).</summary>
    Proven,

    /// <summary>Guessed, with nothing available to confirm it. May be right; cannot be claimed.</summary>
    Assumed,

    /// <summary>Knowingly not the original input (decompiled source, a stand-in assembly).</summary>
    Substituted,

    /// <summary>Needed by the compilation and not recovered at all.</summary>
    Missing,
}

/// <summary>What the ledger as a whole says about reproducing the original bytes.</summary>
public enum ReproductionOutlook
{
    /// <summary>Every input is recorded, derived, inferred or proven.</summary>
    Exact,

    /// <summary>Nothing is knowingly wrong, but some inputs are guesses nothing confirmed.</summary>
    Unconfirmed,

    /// <summary>At least one input is knowingly not the original, or is missing outright.</summary>
    Impossible,
}

/// <summary>
/// One input of the reconstructed compilation - a source document, a reference, a compiler
/// flag - and the evidence behind it. <paramref name="Count"/> lets a uniform group of inputs
/// share an entry, so the ledger stays readable when 163 references all resolved the same way.
/// </summary>
public record ReconstructionEntry(
    string Category,
    string Name,
    InputEvidence Evidence,
    string Detail,
    int Count = 1);

/// <summary>
/// The record of how each input of a reconstructed compilation was obtained.
///
/// Written next to the complog as {package}.{version}.reconstruction.json. It deliberately
/// carries no timestamps or machine paths in its ordering, and entries are emitted in a fixed
/// order, so the same package reconstructs to the same file - which makes it a golden file:
/// diff two runs and the diff is exactly the change in reconstruction quality.
/// </summary>
public class ReconstructionLedger
{
    public const string CategorySymbols = "symbols";
    public const string CategoryCompiler = "compiler";
    public const string CategoryOption = "option";
    public const string CategorySource = "source";
    public const string CategoryReference = "reference";
    public const string CategoryGenerator = "generator";
    public const string CategorySigning = "signing";

    private static readonly string[] CategoryOrder =
    [
        CategorySymbols, CategoryCompiler, CategoryOption, CategorySource,
        CategoryReference, CategoryGenerator, CategorySigning,
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        // The detail lines are prose meant to be read in a diff; the default encoder escapes
        // apostrophes and angle brackets into \uXXXX, which makes them unreadable.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly List<ReconstructionEntry> _entries = [];

    public string? Package { get; private set; }
    public string? TargetFramework { get; private set; }
    public string? Assembly { get; private set; }

    /// <summary>Names the compilation the ledger describes; shown in the written file.</summary>
    public void Describe(string package, string? targetFramework, string? assembly)
    {
        Package = package;
        TargetFramework = targetFramework;
        Assembly = assembly;
    }

    public void Add(string category, string name, InputEvidence evidence, string detail, int count = 1)
    {
        if (count > 0)
        {
            _entries.Add(new ReconstructionEntry(category, name, evidence, detail, count));
        }
    }

    /// <summary>
    /// Overwrites what an earlier stage said about an input. The complog is built before the
    /// rebuild runs, so verify knows things complog creation could only predict - which
    /// compiler and runtime actually hosted the rebuild, above all.
    /// </summary>
    public void Replace(string category, string name, InputEvidence evidence, string detail, int count = 1)
    {
        _entries.RemoveAll(e =>
            string.Equals(e.Category, category, StringComparison.Ordinal) &&
            string.Equals(e.Name, name, StringComparison.Ordinal));
        Add(category, name, evidence, detail, count);
    }

    public void Recorded(string category, string name, string detail, int count = 1) =>
        Add(category, name, InputEvidence.Recorded, detail, count);

    public void Derived(string category, string name, string detail, int count = 1) =>
        Add(category, name, InputEvidence.Derived, detail, count);

    public void Inferred(string category, string name, string detail, int count = 1) =>
        Add(category, name, InputEvidence.Inferred, detail, count);

    public void Proven(string category, string name, string detail, int count = 1) =>
        Add(category, name, InputEvidence.Proven, detail, count);

    public void Assumed(string category, string name, string detail, int count = 1) =>
        Add(category, name, InputEvidence.Assumed, detail, count);

    public void Substituted(string category, string name, string detail, int count = 1) =>
        Add(category, name, InputEvidence.Substituted, detail, count);

    public void Missing(string category, string name, string detail, int count = 1) =>
        Add(category, name, InputEvidence.Missing, detail, count);

    /// <summary>Entries in a fixed order, so two runs of the same package produce the same file.</summary>
    public IReadOnlyList<ReconstructionEntry> Entries => _entries
        .OrderBy(e => Array.IndexOf(CategoryOrder, e.Category) is var i && i >= 0 ? i : CategoryOrder.Length)
        .ThenBy(e => e.Category, StringComparer.Ordinal)
        .ThenBy(e => e.Evidence)
        .ThenBy(e => e.Name, StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// The inputs that stop the rebuild being a faithful replay, worst first. Named entries come
    /// before rolled-up ones: "ByteString.cs" tells the reader more than "84 further documents".
    /// </summary>
    public IReadOnlyList<ReconstructionEntry> Blockers => Entries
        .Where(e => e.Evidence is InputEvidence.Missing or InputEvidence.Substituted or InputEvidence.Assumed)
        .OrderByDescending(e => e.Evidence == InputEvidence.Missing)
        .ThenByDescending(e => e.Evidence == InputEvidence.Substituted)
        .ThenBy(e => e.Count > 1)
        .ToList();

    public ReproductionOutlook Outlook =>
        _entries.Any(e => e.Evidence is InputEvidence.Missing or InputEvidence.Substituted)
            ? ReproductionOutlook.Impossible
            : _entries.Any(e => e.Evidence == InputEvidence.Assumed)
                ? ReproductionOutlook.Unconfirmed
                : ReproductionOutlook.Exact;

    /// <summary>Inputs covered per evidence kind, counting a rolled-up entry as its Count.</summary>
    public IReadOnlyDictionary<InputEvidence, int> Totals => _entries
        .GroupBy(e => e.Evidence)
        .ToDictionary(g => g.Key, g => g.Sum(e => e.Count));

    public async Task SaveAsync(string path)
    {
        var document = new
        {
            Package,
            TargetFramework,
            Assembly,
            Outlook,
            Totals = Totals.OrderBy(kvp => kvp.Key)
                .ToDictionary(kvp => JsonNamingPolicy.CamelCase.ConvertName(kvp.Key.ToString()), kvp => kvp.Value),
            Inputs = Entries,
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document, JsonOptions));
    }

    /// <summary>
    /// Prints the counts, then every input that is not the original. The detail lines are the
    /// point: "163 references" is worth nothing next to "this one is a different build".
    /// </summary>
    public void Render(IConsoleWriter console, string? savedPath = null, int maxBlockers = 8)
    {
        console.WriteLine();
        console.MarkupLine("[yellow]Reconstruction ledger[/]");

        foreach (var (evidence, count) in Totals.OrderBy(kvp => kvp.Key))
        {
            console.MarkupLine($"  [dim]{count,5}[/]  {Describe(evidence)}");
        }

        switch (Outlook)
        {
            case ReproductionOutlook.Exact:
                console.MarkupLine("  [green]✓[/] Every input is accounted for - the rebuild should be byte-for-byte");
                break;
            case ReproductionOutlook.Unconfirmed:
                console.MarkupLine("  [yellow]⚠[/] Some inputs are guesses nothing could confirm - a byte-for-byte " +
                                   "rebuild is possible but not claimed:");
                break;
            case ReproductionOutlook.Impossible:
                console.MarkupLine("  [yellow]⚠[/] Some inputs are knowingly not the original - the rebuild will " +
                                   "compile but cannot match:");
                break;
        }

        foreach (var entry in Blockers.Take(maxBlockers))
        {
            var count = entry.Count > 1 ? $" (×{entry.Count})" : "";
            console.MarkupLine(
                $"    [dim]•[/] {Escape(entry.Category)} {Escape(entry.Name)}{count}: {Escape(entry.Detail)}");
        }
        if (Blockers.Count > maxBlockers)
        {
            console.MarkupLine($"    [dim]... and {Blockers.Count - maxBlockers} more[/]");
        }

        if (savedPath != null)
        {
            console.MarkupLine($"  [dim]Full ledger: {Escape(savedPath)}[/]");
        }
    }

    private static string Describe(InputEvidence evidence) => evidence switch
    {
        InputEvidence.Recorded => "[green]recorded[/]     read from the package and verified",
        InputEvidence.Derived => "[green]derived[/]      computed from recorded data",
        InputEvidence.Inferred => "[cyan]inferred[/]     recovered from evidence in the shipped assembly",
        InputEvidence.Proven => "[green]proven[/]       searched for, then confirmed against the package",
        InputEvidence.Assumed => "[yellow]assumed[/]      guessed, with nothing to confirm it",
        InputEvidence.Substituted => "[yellow]substituted[/]  knowingly not the original input",
        InputEvidence.Missing => "[red]missing[/]      needed and not recovered",
        _ => evidence.ToString(),
    };

    private static string Escape(string value) => value.Replace("[", "[[").Replace("]", "]]");
}
