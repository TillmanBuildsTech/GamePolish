using GamePolish.Core.Paths;

namespace GamePolish.Core.Sources;

/// <summary>
/// Derives the addon folder name to create inside a game's mod destination.
/// </summary>
/// <remarks>
/// <para>
/// The name matters more than it looks: WoW's addon loader matches the folder against the <c>.toc</c>
/// inside it, so an addon named wrongly is an addon that silently does not load. Hence the precedence -
/// an explicitly expected name first (the registry or the user knows best), then the archive's own
/// <c>.toc</c>, and only then the source URL or file name.
/// </para>
/// <para>
/// This is a naming rule, not a path check: separators, <c>..</c>, reserved device names, a trailing dot
/// and containment are <c>204</c>'s, and every name here goes through it. The one rule that lives here is
/// the <c>.game-polish-</c> prefix, because it is this component's own staging model that reserves it: the
/// install pipeline moves a half-done addon aside as <c>.game-polish-old-&lt;name&gt;-&lt;ts&gt;</c>, and a
/// startup sweep deletes those. An addon installing itself under that prefix would be deleted by the next
/// launch, so the name is refused rather than trimmed.
/// </para>
/// </remarks>
public static class AddonFolderName
{
    /// <summary>Prefix Game Polish reserves for its own directories inside a game's mod folder.</summary>
    public const string ReservedPrefix = ".game-polish-";

    /// <summary>The extension that names an addon.</summary>
    public const string TocExtension = ".toc";

    /// <summary>
    /// Picks the folder name: <paramref name="expectedFolderName"/> when given, else the single <c>.toc</c>
    /// at the staged root, else the name implied by <paramref name="fallbackName"/>.
    /// </summary>
    /// <param name="pathValidator">The validator every untrusted name goes through.</param>
    /// <param name="destinationRoot">An existing directory the folder will be created in.</param>
    /// <param name="stagedRoot">The staged tree, read for <c>.toc</c> files.</param>
    /// <param name="expectedFolderName">A registry- or user-supplied name, or <see langword="null"/>.</param>
    /// <param name="fallbackName">A repository URL or archive file name to fall back to.</param>
    /// <exception cref="PathValidationException">The chosen name is refused; the failure names the rule.</exception>
    public static string Resolve(
        IPathValidator pathValidator,
        string destinationRoot,
        string stagedRoot,
        string? expectedFolderName,
        string fallbackName)
    {
        ArgumentNullException.ThrowIfNull(pathValidator);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedRoot);

        string chosen = string.IsNullOrWhiteSpace(expectedFolderName)
            ? FromStagedToc(stagedRoot) ?? FromSourceName(fallbackName)
            : expectedFolderName;

        return Validate(pathValidator, destinationRoot, chosen);
    }

    /// <summary>
    /// The stem of the single <c>.toc</c> at the staged root, or <see langword="null"/> when there is not
    /// exactly one.
    /// </summary>
    /// <param name="stagedRoot">The staged tree.</param>
    /// <remarks>
    /// A repository or archive with several <c>.toc</c> files at its root is a multi-addon source. The MVP
    /// installs one addon, so the ambiguity is not resolved - it falls through to the source name, which is
    /// at least predictable. Recorded as a known limitation rather than guessed at: picking one of five
    /// addons by directory order would be a coin toss presented as a decision.
    /// </remarks>
    public static string? FromStagedToc(string stagedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedRoot);

        if (!Directory.Exists(stagedRoot))
        {
            return null;
        }

        // Enumerated and compared case-insensitively rather than with the "* .toc" search pattern, so a
        // repository that ships "PFUI.TOC" behaves the same on macOS as on Windows.
        string[] tocs =
        [
            .. Directory.EnumerateFiles(stagedRoot, "*", SearchOption.TopDirectoryOnly)
                .Where(file => Path.GetExtension(file).Equals(TocExtension, StringComparison.OrdinalIgnoreCase)),
        ];

        return tocs.Length == 1 ? Path.GetFileNameWithoutExtension(tocs[0]) : null;
    }

    /// <summary>
    /// The name implied by a repository URL or an archive's file name:
    /// <c>https://github.com/brues-code/pfUI.git</c> and <c>pfUI-main.zip</c> both give <c>pfUI</c>.
    /// </summary>
    /// <param name="sourceName">A URL, or a file name.</param>
    /// <remarks>
    /// A GitHub archive's <c>-master</c>/<c>-main</c> suffix is trimmed because it names the branch, not
    /// the addon, and the folder must match the <c>.toc</c>. The result may still contain nothing usable -
    /// an empty name is refused by the validator, which is better than inventing a fallback.
    /// </remarks>
    public static string FromSourceName(string sourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        string name = sourceName.TrimEnd('/', '\\');
        int lastSeparator = name.LastIndexOfAny(['/', '\\']);

        if (lastSeparator >= 0)
        {
            name = name[(lastSeparator + 1)..];
        }

        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }
        else if (Path.GetExtension(name).ToLowerInvariant() is ".zip" or ".7z" or ".rar" or ".tar" or ".gz")
        {
            name = Path.GetFileNameWithoutExtension(name);
        }

        foreach (string suffix in new[] { "-master", "-main" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];

                break;
            }
        }

        return name;
    }

    /// <summary>True when <paramref name="name"/> claims the prefix Game Polish reserves.</summary>
    /// <param name="name">A candidate folder name.</param>
    public static bool IsReserved(string? name) =>
        name is not null && name.StartsWith(ReservedPrefix, PathComparer.PathComparison);

    /// <summary>
    /// Validates a chosen name and returns it: <c>204</c> for everything about it as a path, and this class
    /// for the reserved prefix.
    /// </summary>
    private static string Validate(IPathValidator pathValidator, string destinationRoot, string candidate)
    {
        // The composition (and the refusal of separators, '..', reserved devices, trailing dots) is 204's;
        // the resolved path's file name is the validated name.
        string resolved = pathValidator.ResolveSafeDirectoryName(destinationRoot, candidate);
        string name = Path.GetFileName(resolved);

        if (IsReserved(name))
        {
            throw new PathValidationException(
            [
                new PathValidationFailure(
                    name,
                    UnsafePathReason.ReservedPrefix,
                    $"'{name}' starts with '{ReservedPrefix}', which Game Polish reserves for its own "
                    + "swap directories inside a mod folder. An addon that used it would be deleted by the "
                    + "next startup sweep."),
            ]);
        }

        return name;
    }
}
