namespace GamePolish.Core.Update;

/// <summary>
/// The answer to "is there a newer version of this mod?", in the shape the Library row and its
/// Update Available sheet consume.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type is the whole UI contract for <see cref="IUpdatePipeline.CheckAsync"/>.</b> A view must be
/// able to render the row from these five fields alone - no second call, no probing of the source itself -
/// because the check is the expensive half and the row is repainted on every scroll.
/// </para>
/// <para>
/// <b><see cref="UpdateState.Unknown"/> must render as nothing, not as "up to date".</b> It is the answer for
/// an archive-sourced mod that has no upstream, and for a record whose version was never written. Treating
/// those as current would tell the user their mod is the latest when the app has no idea.
/// </para>
/// <para>
/// <see cref="InstalledVersion"/> is echoed back rather than left to the caller: a check runs against a
/// snapshot of the manifest, and a row that paired this answer with a version it read at a different moment
/// could claim "update available" for a version that is already gone.
/// </para>
/// </remarks>
public sealed record UpdateAvailability
{
    /// <summary>The mod this answer is about.</summary>
    public required string ModId { get; init; }

    /// <summary>The version the manifest records, e.g. a 40-character commit SHA.</summary>
    public required string InstalledVersion { get; init; }

    /// <summary>
    /// What the source serves now, when it was determined. Set for
    /// <see cref="UpdateState.UpdateAvailable"/> (and for <see cref="UpdateState.UpToDate"/>, where it equals
    /// <see cref="InstalledVersion"/>); <see langword="null"/> otherwise.
    /// </summary>
    public string? LatestVersion { get; init; }

    /// <summary>Which of the four answers this is.</summary>
    public required UpdateState State { get; init; }

    /// <summary>
    /// Why the state is <see cref="UpdateState.Unknown"/> or <see cref="UpdateState.CheckFailed"/>, in the
    /// user's language. <see langword="null"/> for the two states that stand on their own.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>True when the row should turn its version tag amber and show the up-arrow.</summary>
    public bool HasUpdate => State == UpdateState.UpdateAvailable;

    /// <summary>True when the check could not answer, for any reason. The row renders no badge.</summary>
    public bool IsIndeterminate => State is UpdateState.Unknown or UpdateState.CheckFailed;

    /// <summary>The answer for a mod that is already at the version the source serves.</summary>
    /// <param name="modId">The mod.</param>
    /// <param name="version">The installed version, which is also the latest.</param>
    public static UpdateAvailability UpToDate(string modId, string version) => new()
    {
        ModId = modId,
        InstalledVersion = version,
        LatestVersion = version,
        State = UpdateState.UpToDate,
    };

    /// <summary>The answer for a mod whose source serves something else.</summary>
    /// <param name="modId">The mod.</param>
    /// <param name="installed">What the manifest records.</param>
    /// <param name="latest">What the source serves now.</param>
    public static UpdateAvailability Available(string modId, string installed, string latest) => new()
    {
        ModId = modId,
        InstalledVersion = installed,
        LatestVersion = latest,
        State = UpdateState.UpdateAvailable,
    };

    /// <summary>
    /// There is nothing to compare against. The row must render no badge at all - see the remark on this
    /// type.
    /// </summary>
    /// <param name="modId">The mod.</param>
    /// <param name="installed">What the manifest records, possibly empty.</param>
    /// <param name="reason">Why the check cannot answer, in the user's language.</param>
    public static UpdateAvailability Unknown(string modId, string installed, string reason) => new()
    {
        ModId = modId,
        InstalledVersion = installed,
        State = UpdateState.Unknown,
        Reason = reason,
    };

    /// <summary>The check itself failed. The row shows its neutral tag and offers a retry.</summary>
    /// <param name="modId">The mod.</param>
    /// <param name="installed">What the manifest records.</param>
    /// <param name="reason">The mapped failure, so the toast and the log agree.</param>
    public static UpdateAvailability Failed(string modId, string installed, string reason) => new()
    {
        ModId = modId,
        InstalledVersion = installed,
        State = UpdateState.CheckFailed,
        Reason = reason,
    };

    /// <inheritdoc/>
    public override string ToString() => State switch
    {
        UpdateState.UpdateAvailable => $"{ModId}: {InstalledVersion} -> {LatestVersion}",
        UpdateState.UpToDate => $"{ModId}: up to date at {InstalledVersion}",
        _ => $"{ModId}: {State} ({Reason})",
    };
}
