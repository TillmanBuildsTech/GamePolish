using GamePolish.Core.Manifest;

namespace GamePolish.Core.Sources;

/// <summary>Everything the acquirer needs to materialise one mod source into a staging directory.</summary>
/// <remarks>
/// <para>
/// Written as required init-only properties rather than as a positional record, unlike the sketch in the
/// work order: nine positional arguments, five of them strings, is a shape where a call site can put the
/// branch where the folder name belongs and still compile. The install pipeline (<c>208</c>) is the
/// caller, and it is the only one, so naming the fields at the call site costs nothing.
/// </para>
/// <para>
/// <see cref="DataRoot"/> is carried rather than assumed so the request is self-describing, but the
/// acquirer refuses a request whose data root is not its own: staging that lives under a different root
/// than the manifest and the backups could never be swept, and the orphan would be invisible.
/// </para>
/// </remarks>
public sealed record SourceRequest
{
    /// <summary>Default for <see cref="TimeoutSeconds"/>: generous enough for a large repository on a slow line.</summary>
    public const int DefaultTimeoutSeconds = 120;

    /// <summary>The game the mod is for, e.g. <c>octowow</c>. A single directory name under the data root.</summary>
    public required string GameId { get; init; }

    /// <summary>
    /// For <see cref="ModSourceType.Github"/>, the repository URL. For <see cref="ModSourceType.Zip"/>,
    /// the archive's original file name (what the manifest stores as the mod's source).
    /// </summary>
    public required string SourceUrl { get; init; }

    /// <summary>Which kind of source <see cref="SourceUrl"/> names.</summary>
    public required ModSourceType SourceType { get; init; }

    /// <summary>
    /// The branch to clone, or <see langword="null"/> for the remote's default branch - which is what the
    /// design note's <c>BRANCH</c> placeholder means.
    /// </summary>
    public string? Branch { get; init; }

    /// <summary>
    /// The folder name to create, when the registry or the user already knows it. Wins over the name
    /// derived from the archive or the source, after the path validator has accepted it.
    /// </summary>
    public string? ExpectedFolderName { get; init; }

    /// <summary>This installation's data root, e.g. <c>C:\Users\me\.game-polish</c>.</summary>
    public required string DataRoot { get; init; }

    /// <summary>
    /// Identifies the staging directory, <c>&lt;data root&gt;/&lt;game&gt;/staging/&lt;OperationId&gt;</c>,
    /// so a crashed run's leftovers can be named and swept.
    /// </summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Filled in by task <c>504</c> (the GitHub PAT editor); always <see langword="null"/> until then.
    /// Nothing reads it yet, and it must never reach a URL that gets stored: the manifest refuses a
    /// source carrying an embedded credential.
    /// </summary>
    public string? Credential { get; init; }

    /// <summary>How long a single git invocation may take. Must be positive.</summary>
    public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;
}
