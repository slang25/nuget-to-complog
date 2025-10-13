namespace NuGetToCompLog.Commands;

/// <summary>
/// Command to process a NuGet package and create a CompLog file.
/// </summary>
public record ProcessPackageCommand(string PackageId, string? Version = null);
