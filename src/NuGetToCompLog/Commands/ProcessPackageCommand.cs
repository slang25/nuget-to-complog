namespace NuGetToCompLog.Commands;

/// <summary>
/// Command to process a NuGet package and create a CompLog file.
/// </summary>
/// <param name="Assembly">Which assembly to capture when the package ships several for one TFM.</param>
/// <param name="RunGenerators">
/// Whether source generators found for the package may be executed in this process to prove
/// they reproduce the recorded documents.
/// </param>
public record ProcessPackageCommand(
    string PackageId,
    string? Version = null,
    string? Assembly = null,
    bool RunGenerators = true);
