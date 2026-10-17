namespace GamePolish.Core.Registry;

/// <summary>What kind of diagnostic a <see cref="RegistryWarning"/> is.</summary>
public enum RegistryWarningCode
{
    /// <summary>
    /// A mod's source is an <c>https://</c> URL, but not on github.com. Not a failure: a mirror is
    /// installable, and the incumbent manager's inability to handle one is a reason this app exists.
    /// </summary>
    NonGithubSource,

    /// <summary>The registry could not be fetched: a network error, a timeout, or a non-2xx status.</summary>
    FetchFailed,

    /// <summary>A received payload failed validation and the cached copy is being served instead.</summary>
    PayloadRejected,

    /// <summary>The cache could not be written. The fetch itself succeeded and its result was returned.</summary>
    CacheWriteFailed,

    /// <summary>The cache file exists but could not be read, or is for another game or schema version.</summary>
    CacheUnreadable,
}

/// <summary>
/// A warning-level diagnostic from <see cref="RegistryService"/>, handed to the optional sink the
/// service is constructed with. Never thrown, never fatal.
/// </summary>
/// <remarks>
/// <para>
/// Core has no logger, and task 212 owns the logging abstraction, so this is a plain value the caller
/// decides what to do with: the app passes a lambda that writes it to 212's logger, and a test passes
/// one that appends to a list. That keeps every "we carried on, but you should know" path
/// (<see cref="RegistryWarningCode.NonGithubSource"/>, a cache write that failed, a payload rejected
/// while a good cache was served) observable without this task inventing a logging framework.
/// </para>
/// <para>
/// Warnings are raised on the thread that runs the fetch, which for a call that joins another caller's
/// in-flight fetch is not necessarily the caller's own. A sink that logs must therefore be thread-safe,
/// and must not throw: an exception from the sink would replace a successful result with a failure.
/// </para>
/// </remarks>
public sealed record RegistryWarning
{
    /// <summary>Which diagnostic this is.</summary>
    public required RegistryWarningCode Code { get; init; }

    /// <summary>The log line: what happened, and what was done about it.</summary>
    public required string Message { get; init; }

    /// <summary>The mod the warning belongs to, when it belongs to one.</summary>
    public string? ModId { get; init; }

    /// <inheritdoc/>
    public override string ToString() => ModId is null ? $"{Code}: {Message}" : $"{Code} ({ModId}): {Message}";
}
