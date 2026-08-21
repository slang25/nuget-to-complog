using System.Text.Json;
using NuGetToCompLog.Services.Reconstruction;
using Xunit;

namespace NuGetToCompLog.Tests;

/// <summary>
/// The ledger is the tool's honesty about what a NuGet package does not record. Its verdict has
/// to be conservative (a guess is never a claim) and its file has to be stable, because a
/// committed ledger is how a change in reconstruction quality shows up as a diff.
/// </summary>
public class ReconstructionLedgerTests
{
    [Fact]
    public void EveryInputAccountedForIsAnExactOutlook()
    {
        var ledger = new ReconstructionLedger();
        ledger.Recorded(ReconstructionLedger.CategorySource, "embedded in PDB", "checksum verified", 40);
        ledger.Proven(ReconstructionLedger.CategoryReference, "matched by recorded MVID", "module id equal", 12);
        ledger.Inferred(ReconstructionLedger.CategoryOption, "/features:nullablePublicOnly", "attribute present");
        ledger.Derived(ReconstructionLedger.CategoryOption, "/pathmap", "from document paths");

        Assert.Equal(ReproductionOutlook.Exact, ledger.Outlook);
        Assert.Empty(ledger.Blockers);
    }

    /// <summary>
    /// A guess that nothing confirmed might still be the original input - it just cannot be
    /// claimed, which is a weaker statement than "this will not match".
    /// </summary>
    [Fact]
    public void AnUnconfirmedGuessIsNotTheSameAsAKnownSubstitution()
    {
        var unconfirmed = new ReconstructionLedger();
        unconfirmed.Recorded(ReconstructionLedger.CategorySource, "embedded in PDB", "checksum verified", 40);
        unconfirmed.Assumed(ReconstructionLedger.CategoryReference, "no MVID recorded", "cannot confirm", 3);
        Assert.Equal(ReproductionOutlook.Unconfirmed, unconfirmed.Outlook);

        var substituted = new ReconstructionLedger();
        substituted.Recorded(ReconstructionLedger.CategorySource, "embedded in PDB", "checksum verified", 40);
        substituted.Substituted(ReconstructionLedger.CategorySource, "Foo.cs", "decompiled");
        Assert.Equal(ReproductionOutlook.Impossible, substituted.Outlook);
    }

    [Fact]
    public void MissingInputsMakeReproductionImpossible()
    {
        var ledger = new ReconstructionLedger();
        ledger.Missing(ReconstructionLedger.CategoryReference, "System.IO.Hashing.dll", "found nowhere");

        Assert.Equal(ReproductionOutlook.Impossible, ledger.Outlook);
        Assert.Equal(InputEvidence.Missing, ledger.Blockers[0].Evidence);
    }

    /// <summary>Rolled-up entries stand for their whole group, so totals count inputs, not rows.</summary>
    [Fact]
    public void TotalsCountInputsRatherThanEntries()
    {
        var ledger = new ReconstructionLedger();
        ledger.Recorded(ReconstructionLedger.CategorySource, "from Source Link", "checksum verified", 137);
        ledger.Recorded(ReconstructionLedger.CategorySource, "embedded in PDB", "checksum verified", 2);

        Assert.Equal(139, ledger.Totals[InputEvidence.Recorded]);
    }

    /// <summary>An empty group is not an input; it must not appear as a zero-count row.</summary>
    [Fact]
    public void EmptyGroupsAreNotRecorded()
    {
        var ledger = new ReconstructionLedger();
        ledger.Assumed(ReconstructionLedger.CategoryReference, "no MVID recorded", "cannot confirm", 0);

        Assert.Empty(ledger.Entries);
        Assert.Equal(ReproductionOutlook.Exact, ledger.Outlook);
    }

    /// <summary>
    /// verify learns which compiler actually ran, after complog creation has already guessed.
    /// The ledger must end up with one entry per input, holding the later answer.
    /// </summary>
    [Fact]
    public void ReplaceOverwritesAnEarlierStagesEntry()
    {
        var ledger = new ReconstructionLedger();
        ledger.Assumed(ReconstructionLedger.CategoryCompiler, "4.12.0", "not installed locally");
        ledger.Replace(ReconstructionLedger.CategoryCompiler, "4.12.0", InputEvidence.Proven, "fetched the exact toolset");

        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(InputEvidence.Proven, entry.Evidence);
        Assert.Equal(ReproductionOutlook.Exact, ledger.Outlook);
    }

    /// <summary>
    /// Entry order must not depend on the order acquisition happened to run in, or every ledger
    /// diff would be full of moves.
    /// </summary>
    [Fact]
    public void EntryOrderIsIndependentOfTheOrderInputsWereAdded()
    {
        var forwards = new ReconstructionLedger();
        forwards.Proven(ReconstructionLedger.CategoryReference, "b.dll", "");
        forwards.Recorded(ReconstructionLedger.CategorySource, "a.cs", "");
        forwards.Assumed(ReconstructionLedger.CategoryCompiler, "4.12.0", "");
        forwards.Proven(ReconstructionLedger.CategoryReference, "a.dll", "");

        var backwards = new ReconstructionLedger();
        backwards.Proven(ReconstructionLedger.CategoryReference, "a.dll", "");
        backwards.Assumed(ReconstructionLedger.CategoryCompiler, "4.12.0", "");
        backwards.Recorded(ReconstructionLedger.CategorySource, "a.cs", "");
        backwards.Proven(ReconstructionLedger.CategoryReference, "b.dll", "");

        Assert.Equal(
            forwards.Entries.Select(e => $"{e.Category}/{e.Name}"),
            backwards.Entries.Select(e => $"{e.Category}/{e.Name}"));
        Assert.Equal("compiler/4.12.0", $"{forwards.Entries[0].Category}/{forwards.Entries[0].Name}");
    }

    /// <summary>
    /// The written file is a golden file: same package, same bytes. No timestamps, no machine
    /// paths, no ordering that depends on the run.
    /// </summary>
    [Fact]
    public async Task TheWrittenFileIsStableAcrossRuns()
    {
        var directory = Directory.CreateTempSubdirectory("ledger").FullName;
        try
        {
            var first = Path.Combine(directory, "first.json");
            var second = Path.Combine(directory, "second.json");
            await Build().SaveAsync(first);
            await Build().SaveAsync(second);

            Assert.Equal(await File.ReadAllTextAsync(first), await File.ReadAllTextAsync(second));

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(first));
            var root = document.RootElement;
            Assert.Equal("Serilog/4.4.0", root.GetProperty("package").GetString());
            Assert.Equal("impossible", root.GetProperty("outlook").GetString());
            Assert.Equal(3, root.GetProperty("totals").GetProperty("recorded").GetInt32());
            Assert.Equal(1, root.GetProperty("totals").GetProperty("substituted").GetInt32());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        static ReconstructionLedger Build()
        {
            var ledger = new ReconstructionLedger();
            ledger.Describe("Serilog/4.4.0", "net8.0", "Serilog.dll");
            ledger.Recorded(ReconstructionLedger.CategorySource, "embedded in PDB", "checksum verified", 3);
            ledger.Substituted(ReconstructionLedger.CategorySource, "Missing.cs", "decompiled");
            return ledger;
        }
    }
}
