namespace GamePolish.Core.Paths;

/// <summary>
/// Turns an untrusted relative path from a mod source (a git tree, a ZIP entry, a registry-supplied
/// folder name) plus a trusted destination root into an absolute path proven to be inside that root.
/// </summary>
/// <remarks>
/// Nothing here writes, creates or deletes: it is pure inspection, which is what makes it safe to call
/// before the backup step in every mutation path.
/// </remarks>
public interface IPathValidator
{
    /// <summary>Resolves one untrusted relative path inside a trusted root.</summary>
    /// <param name="destinationRoot">An existing, absolute directory that mod content may be written into.</param>
    /// <param name="untrustedRelativePath">A path from the mod source, taken literally: never decoded, never trimmed.</param>
    /// <returns>The absolute path, proven to be inside <paramref name="destinationRoot"/>.</returns>
    /// <exception cref="PathValidationException">The input is refused; the exception names the rule.</exception>
    string ResolveSafe(string destinationRoot, string untrustedRelativePath);

    /// <summary>Resolves a single untrusted folder name (an addon folder name) inside a trusted root.</summary>
    /// <param name="destinationRoot">An existing, absolute directory the folder may be created in.</param>
    /// <param name="untrustedFolderName">A single name, with no separator of any kind.</param>
    /// <exception cref="PathValidationException">The name is refused; the exception names the rule.</exception>
    string ResolveSafeDirectoryName(string destinationRoot, string untrustedFolderName);

    /// <summary>
    /// Validates a whole tree at once, so that collisions between entries are detectable - the case a
    /// per-file call cannot see.
    /// </summary>
    /// <param name="destinationRoot">An existing, absolute directory mod content may be written into.</param>
    /// <param name="untrustedRelativePaths">Every path the source wants to write.</param>
    /// <returns>One failure per rejected input, plus one per case collision. Empty when the batch is safe.</returns>
    IReadOnlyList<PathValidationFailure> ValidateAll(string destinationRoot, IEnumerable<string> untrustedRelativePaths);
}
