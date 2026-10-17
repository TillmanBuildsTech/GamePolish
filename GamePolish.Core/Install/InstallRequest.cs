using GamePolish.Core.Manifest;

namespace GamePolish.Core.Install;

/// <summary>
/// Everything the install pipeline needs to put one mod into one game: where the source is, what to call
/// it, and whether the caller has agreed to replace files it did not put there.
/// </summary>
/// <remarks>
/// <para>
/// Written as <c>required init</c> properties rather than as the positional record the work order sketches,
/// for the reason task <c>207</c> recorded on <see cref="Sources.SourceRequest"/>: nine positional
/// arguments, five of them strings, is a shape where a call site can put the branch where the display name
/// belongs and still compile. The two callers - the Add Mod flow (<c>304</c>) and the CLI (<c>213</c>) - name
/// what they set, and a test cannot silently transpose two fields.
/// </para>
/// <para>
/// <b>The request is untrusted input.</b> Everything in it that ends up as a path - the folder name derived
/// from the source, every path inside the archive - is validated by <c>204</c> before anything is written,
/// and the source URL is validated by <c>207</c> before a process is started. Nothing here is trusted just
/// because it arrived in this shape.
/// </para>
/// </remarks>
public sealed record InstallRequest
{
    /// <summary>The tracked game to install into, e.g. <c>octowow</c>.</summary>
    /// <remarks>
    /// An untracked game is a failure (<c>GameInstallPathMissing</c>), never an auto-add: the install
    /// pipeline writes into a folder the user has already confirmed is this game's.
    /// </remarks>
    public required string GameId { get; init; }

    /// <summary>Which kind of source this install comes from. It decides which <c>ISourceAcquirer</c> method runs.</summary>
    public required ModSourceType SourceType { get; init; }

    /// <summary>
    /// For <see cref="ModSourceType.Github"/>, the repository URL. For <see cref="ModSourceType.Zip"/> it is
    /// optional - <see cref="ZipFileName"/> is then the source, because that is what the manifest records.
    /// </summary>
    public string SourceUrl { get; init; } = string.Empty;

    /// <summary>The branch to clone, or <see langword="null"/> for the remote's default branch.</summary>
    public string? Branch { get; init; }

    /// <summary>
    /// The archive, for a <see cref="ModSourceType.Zip"/> install. Read once and never disposed or rewound
    /// by the pipeline, and never held longer than the staging call needs it.
    /// </summary>
    public Stream? ZipStream { get; init; }

    /// <summary>
    /// The archive's original file name, for a <see cref="ModSourceType.Zip"/> install. It names the addon
    /// folder when the archive's own <c>.toc</c> does not, and it is what the manifest stores as the mod's
    /// source - a ZIP has no URL.
    /// </summary>
    public string? ZipFileName { get; init; }

    /// <summary>
    /// The name the Library should show, or <see langword="null"/> to use the addon folder name.
    /// </summary>
    /// <remarks>
    /// Display name only: it is never used as a path, and a name full of separators would be harmless here.
    /// The folder name is <c>207</c>'s decision and goes through the validator.
    /// </remarks>
    public string? DisplayName { get; init; }

    /// <summary>
    /// The registry's mod id, or <see langword="null"/> to derive one from the addon folder name.
    /// </summary>
    /// <remarks>
    /// Supplied by the Discover view (<c>304</c>), which knows the registry row it is installing. Deriving one
    /// is what keeps an ad-hoc GitHub URL installable at all - see <c>InstallPipeline</c>'s note on identity.
    /// </remarks>
    public string? ModId { get; init; }

    /// <summary>
    /// True when the user has seen the conflict report and chosen to replace files this mod does not own.
    /// </summary>
    /// <remarks>
    /// The pipeline never asks; the caller does. Without this flag a conflict with another mod's files, or
    /// with files the user made by hand, stops the install before the backup - so the default answer is
    /// always the one that cannot destroy anything.
    /// </remarks>
    public bool AcceptOverwrites { get; init; }
}
