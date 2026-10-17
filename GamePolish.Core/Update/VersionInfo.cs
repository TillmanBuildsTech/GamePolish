namespace GamePolish.Core.Update;

/// <summary>
/// What a source says its current version is: the value to compare, how to read it, and when it was seen.
/// </summary>
/// <param name="Value">
/// The version to show and to compare: a tag name when one sits on the remote's head, otherwise the
/// 40-character commit SHA. A tag is preferred only because it is the string a user recognises - the SHA in
/// <see cref="Sha"/> is what the comparison actually uses.
/// </param>
/// <param name="Kind">How to read <see cref="Value"/>: <c>sha</c> or <c>tag</c>. Never <c>zip</c>.</param>
/// <param name="ObservedAt">When the probe read it, from the injected clock.</param>
/// <remarks>
/// <para>
/// Positional on purpose, unlike the request records elsewhere in Core: three values with no optional
/// members and no string-to-string ambiguity at the call site, which is exactly the shape a value type
/// should have.
/// </para>
/// <para>
/// <b><see cref="Sha"/> is an addition to the work order's sketch, and it is load-bearing.</b> The sketch has
/// a probe answer either a SHA or a tag name in one field, which makes "is the installed SHA the same as the
/// remote's?" unanswerable exactly when a tag sits on the head - the case the tag preference creates. The
/// commit the probe resolved is therefore carried alongside the display value, and
/// <see cref="UpdatePipeline"/> compares <see cref="Sha"/> to the manifest's version when both are commit
/// ids. A probe implementation that cannot resolve a commit (a test double, a future non-git source) leaves
/// it <see langword="null"/> and the comparison falls back to <see cref="Value"/>.
/// </para>
/// </remarks>
public sealed record VersionInfo(string Value, string Kind, DateTimeOffset? ObservedAt)
{
    /// <summary>The commit the remote's head (or the requested branch) points at, when it is known.</summary>
    public string? Sha { get; init; }

    /// <summary>True for a 40-character hexadecimal commit id.</summary>
    public bool IsCommit => IsCommitSha(Value);

    /// <inheritdoc/>
    public override string ToString() => $"{Kind} {Value}";

    /// <summary>True for a 40-character hexadecimal commit id.</summary>
    /// <param name="value">The candidate.</param>
    /// <remarks>
    /// Public because the comparison in <see cref="UpdatePipeline"/> has to answer the same question about the
    /// manifest's version, and a second copy of this rule is how a "looks like a SHA" test drifts between two
    /// call sites.
    /// </remarks>
    public static bool IsCommitSha(string? value) =>
        value is { Length: 40 } && value.All(char.IsAsciiHexDigit);
}
