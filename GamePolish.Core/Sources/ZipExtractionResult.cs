using GamePolish.Core.Manifest;

namespace GamePolish.Core.Sources;

/// <summary>What one archive turned into, once its wrapper folder (if any) has been accounted for.</summary>
/// <remarks>
/// <see cref="Files"/> is relative to the extraction root - the folder that is about to become the addon
/// folder - not addon-root-relative like <see cref="StagedSource.Files"/>: the folder's name is not known
/// until the extraction is complete, because an archive with exactly one <c>.toc</c> at its root is what
/// names it.
/// </remarks>
public sealed record ZipExtractionResult
{
    /// <summary>Every extracted file, relative to the extraction root, ordinally sorted.</summary>
    public required IReadOnlyList<InstalledFileEntry> Files { get; init; }

    /// <summary>True when a single top-level wrapper folder was stripped.</summary>
    public required bool StrippedWrapperFolder { get; init; }

    /// <summary>The stripped wrapper folder's name, or <see langword="null"/> when nothing was stripped.</summary>
    public required string? WrapperFolderName { get; init; }
}
