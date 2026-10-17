using GamePolish.Core.Games;

namespace GamePolish.Cli.Commands;

/// <summary>
/// Loads every descriptor file one at a time, keeping the failures instead of stopping at the first one.
/// </summary>
/// <remarks>
/// <see cref="IGameDescriptorLoader.LoadAll"/> is the right call for the app - a bad descriptor means no game can
/// be trusted - but it throws on the first invalid file, and the whole value of <c>descriptors</c> and
/// <c>doctor</c> is that they report <i>every</i> problem in one run. This walks the loader's own
/// <see cref="IGameDescriptorLoader.DescriptorDirectories"/> and calls
/// <see cref="IGameDescriptorLoader.LoadFromFile"/> per file, so the same validation runs and the same messages
/// come back.
/// </remarks>
internal static class DescriptorScan
{
    /// <summary>One descriptor file and what became of it.</summary>
    /// <param name="File">The absolute path of the <c>&lt;id&gt;.yaml</c>.</param>
    /// <param name="Id">The id, from the file name, or the descriptor when it loaded.</param>
    /// <param name="Descriptor">The loaded descriptor, or <see langword="null"/> when it is invalid.</param>
    /// <param name="Problem">The first validation problem, or <see langword="null"/> when it is valid.</param>
    internal sealed record Entry(string File, string Id, GameDescriptor? Descriptor, string? Problem);

    /// <summary>Scans every descriptor directory, returning one entry per file in a deterministic order.</summary>
    /// <param name="descriptors">The loader.</param>
    internal static IReadOnlyList<Entry> Run(IGameDescriptorLoader descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        List<Entry> entries = [];

        foreach (string directory in descriptors.DescriptorDirectories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(directory, "*.yaml").Order(StringComparer.Ordinal))
            {
                try
                {
                    GameDescriptor descriptor = descriptors.LoadFromFile(file);

                    entries.Add(new Entry(file, descriptor.Id, descriptor, null));
                }
                catch (GameDescriptorValidationException exception)
                {
                    string problem = exception.Problems.Count > 0 ? exception.Problems[0] : exception.Message;

                    entries.Add(new Entry(file, Path.GetFileNameWithoutExtension(file), null, problem));
                }
            }
        }

        return entries;
    }
}
