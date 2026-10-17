using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace GamePolish.Core.Operations;

/// <summary>
/// Removes credentials from text before it is written to a log or shown to a user.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place that knows what a secret looks like, and every writer in the app funnels through
/// it: the log redacts on the way to disk and <see cref="OperationResult"/> redacts on the way to the toast.
/// The invariant it serves is absolute - a GitHub PAT, an S3 key or an auth token must never reach a log
/// file, an export or a screenshot of an error dialog - so the redactor is deliberately over-eager. A
/// string that merely looks like a token is replaced with <c>***</c>; losing a little diagnostic text is
/// cheaper than leaking one credential.
/// </para>
/// <para>
/// Two layers, because pattern matching alone is not enough. <see cref="RegisterSecret"/> stores values the
/// app already holds (the PAT from the keychain, an S3 secret) so they are redacted by exact match even if
/// their shape is not recognised; the built-in patterns catch credentials the app has never seen, such as a
/// token embedded in a URL inside a git error message.
/// </para>
/// <para>
/// Registration is <b>in memory only</b>. Storing secrets in a process-wide set is acceptable; writing that
/// set to disk or exporting it would defeat the point, and no method here does. Values shorter than six
/// characters are ignored: a three-character registered string would match inside ordinary words and turn
/// every log line into <c>***</c>. The set grows only as the user configures things, so it stays small.
/// </para>
/// <para>
/// Thread safety matters because the log is written from whichever thread finished an operation while the
/// settings view may register a new token: the patterns are immutable and the registered values live in a
/// concurrent dictionary, snapshotted per call so a registration mid-redaction cannot produce a torn result.
/// </para>
/// </remarks>
public static class SecretRedactor
{
    /// <summary>What every redacted span is replaced with.</summary>
    public const string Placeholder = "***";

    /// <summary>Registered values shorter than this are ignored, so a short string cannot match inside words.</summary>
    private const int MinimumSecretLength = 6;

    private static readonly ConcurrentDictionary<string, byte> RegisteredSecrets = new(StringComparer.Ordinal);

    /// <summary>
    /// The built-in patterns, in the order they are applied. Order is load-bearing: the URL form is
    /// redacted before the key-value catch-all so <c>https://user:pass@host</c> keeps its scheme and host,
    /// and the specific token shapes run before the catch-all so <c>token=ghp_...</c> loses the whole token
    /// rather than only the first whitespace-delimited run.
    /// </summary>
    private static readonly Regex[] Patterns =
    [
        // Credentials embedded in a URL: keep the scheme and the '@' so the line still shows what was
        // attempted and where, and star the user:password pair. This is the shape a clone URL takes when a
        // PAT was pasted into it, which is exactly how a token ends up in a git error message.
        new("(?<scheme>https?)://[^/\\s:@]+:[^@\\s]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)),

        // Bearer tokens, with the prefix kept: "Authorization: Bearer ***" tells a reader what header was
        // involved without repeating the credential.
        new("(?<prefix>\\bbearer)\\s+\\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)),

        // GitHub's token families: ghp_ (personal), gho_ (OAuth), ghu_ (user-to-server), ghs_ (server) and
        // ghr_ (refresh), plus the fine-grained github_pat_ form. Twenty token characters is the shortest
        // body GitHub has ever issued, and the trailing boundary keeps a sentence's punctuation intact.
        new("\\b(?:gh[pousr]_|github_pat_)[A-Za-z0-9_]{20,}\\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)),

        // AWS access key ids are a fixed shape; the secret key has none at all, which is why it must be
        // registered by value as well.
        new("\\bAKIA[0-9A-Z]{16}\\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)),

        // The catch-all for everything with a recognisable key: "password=...", "token: ...",
        // "api_key=...", "authorization: ...". The key and its separator survive so the reader can see what
        // kind of value was removed; only the value becomes ***. A "Bearer" value is left to the pattern above:
        // it has already starred the token, and matching it here would star the scheme word too and leave the
        // line unreadable.
        new(
            "(?<key>\\b(?:token|password|passwd|secret|api[_-]?key|authorization)\\b)(?<separator>\\s*[=:]\\s*)(?!bearer\\b)\\S+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(200)),
    ];

    /// <summary>
    /// Remembers <paramref name="secret"/> so every later <see cref="Redact"/> call replaces it exactly.
    /// </summary>
    /// <param name="secret">The value to remember. Null, blank or shorter than six characters is ignored.</param>
    /// <remarks>
    /// Ignoring short values is not a special case; it is the difference between a redactor that works and
    /// one that destroys its own output. Call this when a credential is loaded from the OS keychain (task
    /// 504 owns the load), so the exact value is known before anything can log it. Calling it twice with the
    /// same value is a no-op, and <c>RegisterSecret(string.Empty)</c> cannot make every string match.
    /// </remarks>
    public static void RegisterSecret(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < MinimumSecretLength)
        {
            return;
        }

        RegisteredSecrets.TryAdd(secret, 0);
    }

    /// <summary>Replaces every registered secret and every recognised credential pattern in <paramref name="text"/>.</summary>
    /// <param name="text">Text about to be logged or shown. Null or blank comes back as an empty string.</param>
    /// <returns>The text with each secret replaced by <see cref="Placeholder"/>. Never null.</returns>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string redacted = text;

        // Longest first: a registered secret that contains another registered secret must disappear whole,
        // or the shorter replacement would leave the longer one's tail behind.
        foreach (string secret in RegisteredSecrets.Keys.OrderByDescending(known => known.Length))
        {
            redacted = redacted.Replace(secret, Placeholder, StringComparison.Ordinal);
        }

        foreach (Regex pattern in Patterns)
        {
            redacted = pattern.Replace(redacted, RedactMatch);
        }

        return redacted;
    }

    /// <summary>True when <paramref name="text"/> contains anything <see cref="Redact"/> would remove.</summary>
    /// <param name="text">Text to inspect. Null or blank contains nothing.</param>
    /// <remarks>
    /// Defined as "redaction would change this string", so the two methods can never disagree about what a
    /// secret is. It is a test and assertion helper, not a fast path - it redacts to answer.
    /// </remarks>
    public static bool ContainsSecret(string? text) =>
        !string.IsNullOrEmpty(text) && !string.Equals(Redact(text), text, StringComparison.Ordinal);

    /// <summary>
    /// Builds a replacement that keeps the recognisable part of a match and stars only the credential.
    /// </summary>
    private static string RedactMatch(Match match)
    {
        string? scheme = match.Groups["scheme"].Success ? match.Groups["scheme"].Value : null;
        if (scheme is not null)
        {
            return $"{scheme}://{Placeholder}@";
        }

        if (match.Groups["prefix"].Success)
        {
            return $"{match.Groups["prefix"].Value} {Placeholder}";
        }

        if (match.Groups["key"].Success)
        {
            return $"{match.Groups["key"].Value}{match.Groups["separator"].Value}{Placeholder}";
        }

        // A bare token with no label: nothing to keep, so the whole match goes.
        return Placeholder;
    }
}
