namespace GamePolish.Core.Update;

/// <summary>
/// What a version check managed to decide about one installed mod, in the vocabulary the Library row
/// branches on.
/// </summary>
/// <remarks>
/// <para>
/// Four answers rather than a nullable version, because "no update" and "I could not ask" are different
/// promises: the first lets the UI stay quiet and confident, and the second must not be rendered as the
/// first. <see cref="Unknown"/> and <see cref="CheckFailed"/> both mean "draw no badge", but they differ in
/// what the user can do about it - nothing, versus retry.
/// </para>
/// <para>
/// The values are explicit and append-only: this enum is part of the contract task 504 and the Library row
/// consume, and its name is what a future log reader parses.
/// </para>
/// </remarks>
public enum UpdateState
{
    /// <summary>The installed version is the version the source serves now. No badge.</summary>
    UpToDate = 0,

    /// <summary>They differ. <see cref="UpdateAvailability.LatestVersion"/> is the one available.</summary>
    UpdateAvailable = 1,

    /// <summary>
    /// There is no upstream to compare with - an archive-sourced mod, or a record with no version. The UI
    /// renders <b>nothing</b>: a false "up to date" would hide a real update.
    /// </summary>
    Unknown = 2,

    /// <summary>The check itself failed: network, authentication, timeout. Retryable, never a mutation.</summary>
    CheckFailed = 3,
}
