namespace GamePolish.Core.Operations;

/// <summary>
/// Why an operation failed, in the vocabulary the UI branches on.
/// </summary>
/// <remarks>
/// <para>
/// This enum replaces string matching on exception messages. The message a user reads is prose that will
/// be reworded; the code is a contract that a view can switch on to choose an icon, a retry button, a
/// "check your connection" hint, or a link into the log drawer. Every code is reachable from exactly one
/// kind of failure, so two different problems never collapse into one branch by accident.
/// </para>
/// <para>
/// <b>Adding a member is additive only.</b> The value is written by name to the operation log and to
/// task 213's <c>--json</c> output, and it is what a future log reader parses; renaming a member would
/// make old lines unreadable. The members are grouped by the layer that produces them, and the numbers
/// are implicit after <see cref="None"/> because a group member inserted in place is an intentional,
/// visible change to the file rather than a silent one to the wire format.
/// </para>
/// </remarks>
public enum OperationErrorCode
{
    /// <summary>No error. The matching status is <see cref="OperationStatus.Succeeded"/>.</summary>
    None = 0,

    /// <summary>
    /// Nothing more specific applies. The message and detail are the explanation - the code exists so a
    /// consumer can say "something went wrong" without inventing a category for it.
    /// </summary>
    Unknown,

    /// <summary>The caller cancelled. Paired with <see cref="OperationStatus.Cancelled"/>, never Failed.</summary>
    Cancelled,

    /// <summary>A step did not finish in time: a git clone, a download, a registry fetch.</summary>
    Timeout,

    /// <summary>
    /// Another writer held the game's manifest or backup index for longer than the operation was willing
    /// to wait. Retryable: nothing is wrong with the data.
    /// </summary>
    ConcurrentWrite,

    /// <summary>
    /// A path from a mod source was refused - traversal, rooted path, reserved device name, and the rest
    /// of <c>UnsafePathReason</c>. The detail names every refused path, not just the first.
    /// </summary>
    PathUnsafe,

    /// <summary>The source names something that does not exist: a missing repository, file or archive.</summary>
    SourceNotFound,

    /// <summary>
    /// The source needs credentials the app does not have. Distinct from <see cref="SourceNotFound"/>
    /// because the fix is "add a GitHub PAT", not "check the URL".
    /// </summary>
    SourceAuthRequired,

    /// <summary>
    /// The source is unusable before any work starts: not an <c>https</c> URL, a URL carrying a
    /// credential, a branch name that would reach git as an option.
    /// </summary>
    SourceInvalid,

    /// <summary>No usable <c>git</c> executable is on <c>PATH</c>.</summary>
    GitNotFound,

    /// <summary>git ran and exited non-zero, or could not be started for a reason other than absence.</summary>
    GitFailed,

    /// <summary>The supplied archive is not a usable mod: corrupt, empty, symlinked or over an expansion cap.</summary>
    ZipInvalid,

    /// <summary>
    /// The archive expands past the configured cap. Separate from <see cref="ZipInvalid"/> because the
    /// user's archive may be perfectly valid - it is simply too large to be a mod, and the message says so.
    /// </summary>
    ZipTooLarge,

    /// <summary>The volume refused the write because it is full.</summary>
    DiskFull,

    /// <summary>The OS refused access: a read-only location, a locked file, a protected folder.</summary>
    PermissionDenied,

    /// <summary>A filesystem call failed for any reason not covered above.</summary>
    IoError,

    /// <summary>
    /// The data root could not be resolved or created - it is a file, a volume root, or unreachable.
    /// Nothing else can proceed, so this is the one failure that is never retryable in place.
    /// </summary>
    DataRootUnavailable,

    /// <summary>A game descriptor file exists but is not usable. The detail lists every problem found.</summary>
    DescriptorInvalid,

    /// <summary>The game id is not in <c>games.json</c>.</summary>
    GameNotFound,

    /// <summary>The game is already tracked. The message names the path it is tracked at.</summary>
    GameAlreadyTracked,

    /// <summary>The chosen folder cannot be a game's install directory.</summary>
    InstallPathInvalid,

    /// <summary>The recorded install directory no longer exists - the game was moved, uninstalled or renamed.</summary>
    GameInstallPathMissing,

    /// <summary>The descriptor's verify file is not in the chosen folder, so it is a different game's directory.</summary>
    GameVerifyFileMissing,

    /// <summary>The manifest already records this mod.</summary>
    ModAlreadyInstalled,

    /// <summary>The manifest does not record this mod, so there is nothing to update, disable or remove.</summary>
    ModNotInstalled,

    /// <summary>The mod's folder is gone from the game directory while the manifest still lists it.</summary>
    ModFolderMissing,

    /// <summary>A staged copy the operation expected to move into place has disappeared.</summary>
    StagedCopyMissing,

    /// <summary>Two mods claim the same file and the conflict was not resolved before the write.</summary>
    ConflictsUnresolved,

    /// <summary>The installed version already matches what the source offers; the update was skipped.</summary>
    ModAlreadyUpToDate,

    /// <summary>The source does not state a version, so "is this newer?" cannot be answered.</summary>
    VersionUnknown,

    /// <summary>The snapshot that must precede a write could not be taken, so the write was not attempted.</summary>
    BackupFailed,

    /// <summary>The requested backup timestamp is not in the index.</summary>
    BackupNotFound,

    /// <summary>A restore failed after its own pre-restore snapshot; the previous state is still recoverable.</summary>
    RestoreFailed,

    /// <summary>The record of installed mods could not be read; it was moved aside, not deleted.</summary>
    ManifestCorrupt,

    /// <summary>The manifest exists but could not be read at all - a permissions or I/O failure, not a parse failure.</summary>
    ManifestUnreadable,

    /// <summary>No usable network connection, or the host could not be reached at all.</summary>
    NetworkUnavailable,

    /// <summary>The registry fetch failed. Cached data, when present, is still usable.</summary>
    RegistryUnavailable,

    /// <summary>The registry was fetched but is not a usable registry document.</summary>
    RegistryInvalid,
}
