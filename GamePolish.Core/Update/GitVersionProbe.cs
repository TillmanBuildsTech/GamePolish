using GamePolish.Core.Sources;

namespace GamePolish.Core.Update;

/// <summary>
/// The production version probe: one <c>git ls-remote</c> against the mod's source, parsed into a commit and
/// an optional tag name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <c>ls-remote</c> rather than the work order's "shallow fetch into a scratch clone".</b> The sketch's
/// requirement is that a check must not clone the tree, because the Library calls this per mod; a
/// <c>git ls-remote</c> is a strictly cheaper way to satisfy it. It is one network round trip that asks the
/// remote for its refs and transfers no objects at all, so there is no working directory, no scratch
/// repository to name, and - the part that matters - nothing to clean up, on success or on failure. The
/// staging-then-measure fallback the work order allows exists for the case where <c>207</c> is the only tool
/// available; it is not the case here, because <c>207</c> left its git invocation in a reusable
/// <see cref="GitRunner"/>.
/// </para>
/// <para>
/// <b>What a probe cannot give you.</b> No tags are fetched by the staging clone (<c>--no-tags</c>, decided in
/// <c>207</c>), so an installed git mod's version is always a SHA. This probe therefore reports the SHA as
/// the comparison and a tag name only as the label to show, which is why
/// <see cref="VersionInfo.Sha"/> exists. "Which version is newer" - semver, release notes, changelogs - is
/// task 503's job; this answers "what is upstream's head?" and nothing more.
/// </para>
/// <para>
/// <b>What it does not do.</b> It runs no hook and checks nothing out, so invariant #2 (a repository can never
/// run code here) has nothing to enforce beyond the URL: the only paths git reads are its own network
/// transport. It runs without <c>207</c>'s hardening block for the same reason - there is no working tree for
/// <c>core.hooksPath</c> or <c>core.symlinks</c> to affect - while <see cref="GitRunner"/> still applies
/// <c>GIT_CONFIG_NOSYSTEM</c>, <c>GIT_TERMINAL_PROMPT=0</c> and <c>GIT_ASKPASS</c> to every call, so a
/// credential prompt can never block a check that runs in the background.
/// </para>
/// </remarks>
public sealed class GitVersionProbe : IVersionProbe
{
    /// <summary><see cref="VersionInfo.Kind"/> for a commit id.</summary>
    public const string CommitKind = "sha";

    /// <summary><see cref="VersionInfo.Kind"/> for a tag name.</summary>
    public const string TagKind = "tag";

    /// <summary>How long one <c>ls-remote</c> may take before its process tree is killed.</summary>
    public const int DefaultTimeoutSeconds = 30;

    /// <summary>Longest branch name accepted; the same rule <c>207</c> applies before a name reaches git.</summary>
    private const int MaximumBranchLength = 101;

    private readonly GitRunner _git;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _timeout;

    /// <summary>Initializes the probe with the real <c>git</c> from <c>PATH</c> and the system clock.</summary>
    public GitVersionProbe()
        : this(new GitRunner(), TimeProvider.System, DefaultTimeoutSeconds)
    {
    }

    /// <summary>Initializes the probe with injected collaborators.</summary>
    /// <param name="git">The git runner; its launcher is the process seam tests replace.</param>
    /// <param name="timeProvider">The clock behind <see cref="VersionInfo.ObservedAt"/>.</param>
    /// <param name="timeoutSeconds">How long one probe may take. Must be positive.</param>
    public GitVersionProbe(GitRunner? git = null, TimeProvider? timeProvider = null, int timeoutSeconds = DefaultTimeoutSeconds)
    {
        if (timeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeoutSeconds),
                timeoutSeconds,
                "A version probe needs a positive timeout; zero would fail every check before git started.");
        }

        _git = git ?? new GitRunner();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _timeout = TimeSpan.FromSeconds(timeoutSeconds);
    }

    /// <inheritdoc/>
    public async Task<VersionInfo> GetRemoteVersionAsync(
        string sourceUrl,
        string? branch,
        CancellationToken cancellationToken)
    {
        string url = ValidateSource(sourceUrl);
        string reference = ValidateReference(branch);

        cancellationToken.ThrowIfCancellationRequested();

        GitProcessResult result = await _git.RunAsync(
            BuildCommand(url, reference),
            workingDirectory: null,
            hardening: null,
            _timeout,
            cancellationToken).ConfigureAwait(false);

        RefListing listing = RefListing.Parse(result.StandardOutput);
        string sha = listing.ShaOf(reference)
            ?? throw new SourceAcquisitionException(
                SourceAcquisitionFailure.Unspecified,
                $"'{url}' did not report a commit for '{reference}', so its current version cannot be determined. "
                + "An empty repository, or a branch that does not exist upstream, looks like this.");

        string? tag = listing.TagAt(sha);

        return new VersionInfo(tag ?? sha, tag is null ? CommitKind : TagKind, _timeProvider.GetUtcNow())
        {
            Sha = sha,
        };
    }

    /// <summary>
    /// The argv for the probe: options, then <c>--</c>, then the URL and the ref patterns.
    /// </summary>
    /// <param name="url">An already validated <c>https</c> URL.</param>
    /// <param name="reference"><c>HEAD</c>, or <c>refs/heads/&lt;branch&gt;</c>.</param>
    /// <remarks>
    /// <c>--</c> separates options from data even though the URL was validated first: the URL is the only
    /// user-supplied string in this command, and a future rule that lets a host name start with <c>-</c>
    /// should not be able to turn it into an option. The tag pattern is what makes the tag-on-head answer
    /// possible without a second round trip.
    /// </remarks>
    private static IReadOnlyList<string> BuildCommand(string url, string reference) =>
    [
        "ls-remote",
        "--quiet",
        "--",
        url,
        reference,
        "refs/tags/*",
    ];

    /// <summary>
    /// Refuses anything but an <c>https</c> URL with no embedded credential, before git is asked to do
    /// anything.
    /// </summary>
    /// <remarks>
    /// The same two rules <c>207</c> enforces, restated here rather than shared, because the refusal has to
    /// happen before a process is started and this class must not depend on the acquirer's internals. A
    /// credential in a URL is refused for the reason the manifest refuses it: it is a secret, and a check
    /// would put it in an error message.
    /// </remarks>
    private static string ValidateSource(string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            throw Invalid("no source URL was supplied, so there is nothing to ask for a version.");
        }

        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri? uri))
        {
            throw Invalid($"'{sourceUrl}' is not a URL.");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid(
                $"'{sourceUrl}' is a {uri.Scheme}:// URL. A version check reads a repository over https:// only: "
                + "an unauthenticated transport could answer with a different repository than the one installed.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw Invalid($"'{sourceUrl}' carries an embedded credential, which a version check must not read.");
        }

        return sourceUrl;
    }

    /// <summary>The ref pattern to read: <c>HEAD</c> for the default branch, else the branch's ref.</summary>
    private static string ValidateReference(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            return "HEAD";
        }

        string name = branch.Trim();

        bool usable = name.Length <= MaximumBranchLength
            && char.IsAsciiLetterOrDigit(name[0])
            && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '/' or '-');

        return usable
            ? "refs/heads/" + name
            : throw Invalid(
                $"'{branch}' is not a usable branch name, so the remote's version cannot be read. A branch must "
                + $"start with a letter or a digit and contain only letters, digits, '.', '_', '/' or '-', up to "
                + $"{MaximumBranchLength - 1} characters.");
    }

    /// <summary>Builds the refusal for an unusable source, before any process is started.</summary>
    private static SourceAcquisitionException Invalid(string reason) =>
        new(SourceAcquisitionFailure.InvalidSource, $"the version check was refused: {reason}");

    /// <summary>
    /// The parsed <c>ls-remote</c> output: every <c>ref name -&gt; sha</c> pair, with annotated tags reduced to
    /// the commit they point at.
    /// </summary>
    /// <remarks>
    /// An annotated tag is two lines - <c>refs/tags/v5.2</c> for the tag object and <c>refs/tags/v5.2^{}</c>
    /// for the commit - so the peeled form wins whenever it is present. Without that, a tag on the head would
    /// look like it points somewhere else and the label would be dropped for the wrong reason.
    /// </remarks>
    private sealed class RefListing
    {
        private readonly List<(string Sha, string Reference)> _refs = [];
        private readonly Dictionary<string, string> _tags = new(StringComparer.Ordinal);

        public static RefListing Parse(string output)
        {
            RefListing listing = new();

            foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int tab = line.IndexOf('\t', StringComparison.Ordinal);

                if (tab <= 0)
                {
                    continue;
                }

                string sha = line[..tab].Trim();
                string reference = line[(tab + 1)..].Trim();

                if (sha.Length == 0 || reference.Length == 0)
                {
                    continue;
                }

                listing.Add(sha, reference);
            }

            return listing;
        }

        /// <summary>The commit a ref names, or <see langword="null"/> when the remote did not report it.</summary>
        public string? ShaOf(string reference)
        {
            foreach ((string sha, string name) in _refs)
            {
                if (string.Equals(name, reference, StringComparison.Ordinal))
                {
                    return sha;
                }
            }

            return null;
        }

        /// <summary>
        /// The ordinally greatest tag name pointing at <paramref name="sha"/>, or <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// "Greatest" rather than "first" only so the answer is stable between runs; tags are not ordered
        /// semantically here, and deciding which of two tags on one commit is newer is task 503's problem.
        /// </remarks>
        public string? TagAt(string sha)
        {
            string? best = null;

            foreach ((string name, string tagSha) in _tags)
            {
                if (!string.Equals(tagSha, sha, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (best is null || string.CompareOrdinal(name, best) > 0)
                {
                    best = name;
                }
            }

            return best;
        }

        private void Add(string sha, string reference)
        {
            _refs.Add((sha, reference));

            if (!reference.StartsWith("refs/tags/", StringComparison.Ordinal))
            {
                return;
            }

            string name = reference["refs/tags/".Length..];

            if (!name.EndsWith("^{}", StringComparison.Ordinal))
            {
                _tags[name] = sha;

                return;
            }

            // The peeled line names the commit the annotated tag object points at, which is what a checkout
            // would produce; it replaces the tag object's own id.
            name = name[..^3];
            _tags[name] = sha;
        }
    }
}
