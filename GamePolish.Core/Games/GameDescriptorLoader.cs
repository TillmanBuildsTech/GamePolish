using GamePolish.Core.Storage;

using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GamePolish.Core.Games;

/// <summary>Finds and loads game descriptors from the shipping directory and the data root.</summary>
public interface IGameDescriptorLoader
{
    /// <summary>
    /// Directories searched for descriptors, in precedence order: the one shipped next to the app
    /// first, the user-supplied one under the data root second.
    /// </summary>
    IReadOnlyList<string> DescriptorDirectories { get; }

    /// <summary>Loads and validates one descriptor file.</summary>
    /// <param name="path">Path to a <c>&lt;id&gt;.yaml</c> file.</param>
    /// <exception cref="GameDescriptorValidationException">The file is missing, too large, not valid YAML, or invalid.</exception>
    GameDescriptor LoadFromFile(string path);

    /// <summary>Loads the descriptor with this id, or returns <see langword="null"/> when there is none.</summary>
    /// <param name="id">The game id, e.g. <c>octowow</c>.</param>
    /// <exception cref="GameDescriptorValidationException">The matching file is invalid.</exception>
    GameDescriptor? LoadById(string id);

    /// <summary>Loads every descriptor in <see cref="DescriptorDirectories"/>.</summary>
    /// <exception cref="GameDescriptorValidationException">Any descriptor is invalid, or two of them share an id.</exception>
    IReadOnlyList<GameDescriptor> LoadAll();
}

/// <summary>
/// The YAML descriptor loader: <c>games/&lt;id&gt;.yaml</c> to a validated
/// <see cref="GameDescriptor"/>.
/// </summary>
/// <remarks>
/// <para>
/// The deserializer is configured once, and its options are load-bearing decisions:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>UnderscoredNamingConvention</c> maps <c>install_detection</c> to <see cref="GameDescriptor.InstallDetection"/>,
/// so the file reads as the design notes write it.
/// </description></item>
/// <item><description>
/// <c>IgnoreUnmatchedProperties</c> is what "reserved" means for <c>settings_files</c>: a descriptor
/// may carry a key this build does not map yet, and it is ignored rather than fatal.
/// </description></item>
/// <item><description>
/// <c>WithDuplicateKeyChecking</c> turns a duplicated key into a loud failure instead of a silent
/// last-one-wins, and <c>WithMaximumRecursion</c> bounds nested mappings; the file is also capped at
/// <see cref="MaximumDescriptorBytes"/> before parsing.
/// </description></item>
/// <item><description>
/// <c>WithEnforceRequiredMembers</c> is deliberately <b>not</b> used. It would throw the library's
/// own exception on a missing key, while this loader's contract is a
/// <see cref="GameDescriptorValidationException"/> naming the key, the value and the expectation.
/// The <c>required</c> members on the record graph stay a statement about callers, and
/// <see cref="GameDescriptorValidator"/> is what enforces them for YAML input.
/// </description></item>
/// <item><description>
/// Only the record graph is ever deserialized - never <c>Deserialize&lt;object&gt;</c>, never a type
/// chosen by a tag in the document. A descriptor maps to these four records or it fails.
/// </description></item>
/// </list>
/// <para>
/// Descriptors are authored in this project's own repository, not supplied by a mod, so they are not
/// untrusted input in the sense of invariant #3; the size cap and recursion limit are there to keep a
/// mistake cheap, not to sandbox an attacker.
/// </para>
/// </remarks>
public sealed class GameDescriptorLoader : IGameDescriptorLoader
{
    /// <summary>Directory name searched in each root, e.g. <c>&lt;app base dir&gt;/games</c>.</summary>
    public const string DescriptorDirectoryName = "games";

    /// <summary>Largest descriptor file accepted, before parsing.</summary>
    public const long MaximumDescriptorBytes = 1024 * 1024;

    /// <summary>Largest nesting depth accepted in a descriptor document.</summary>
    public const int MaximumNestingDepth = 16;

    private const string DescriptorExtension = ".yaml";

    private readonly IReadOnlyList<string> _directories;
    private readonly IDeserializer _deserializer;

    /// <summary>Initializes a loader over the shipping directory and <paramref name="paths"/>' data root.</summary>
    /// <param name="paths">The resolved data root, for user-supplied descriptors.</param>
    public GameDescriptorLoader(GamePolishPaths paths)
        : this([Path.Combine(AppContext.BaseDirectory, DescriptorDirectoryName), Path.Combine(paths.Root, DescriptorDirectoryName)])
    {
    }

    /// <summary>Initializes a loader over explicit directories, in precedence order.</summary>
    /// <param name="descriptorDirectories">
    /// Directories to search. A directory that does not exist is skipped, so the shipping directory
    /// can be absent in a test host.
    /// </param>
    public GameDescriptorLoader(IEnumerable<string> descriptorDirectories)
    {
        ArgumentNullException.ThrowIfNull(descriptorDirectories);

        _directories = descriptorDirectories.Select(Path.GetFullPath).ToArray();

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .WithDuplicateKeyChecking()
            .WithMaximumRecursion(MaximumNestingDepth)
            .WithTypeConverter(new ReadOnlyListYamlConverter())
            .Build();
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> DescriptorDirectories => _directories;

    /// <inheritdoc/>
    public GameDescriptor LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string descriptorPath = Path.GetFullPath(path);

        if (!File.Exists(descriptorPath))
        {
            throw new GameDescriptorValidationException(
                descriptorPath,
                [$"{descriptorPath}: descriptor: expected a readable <id>.yaml file, but the file does not exist."]);
        }

        long length = new FileInfo(descriptorPath).Length;
        if (length > MaximumDescriptorBytes)
        {
            throw new GameDescriptorValidationException(
                descriptorPath,
                [
                    $"{descriptorPath}: descriptor: expected at most {MaximumDescriptorBytes} bytes, but the file is "
                    + $"{length} bytes. A descriptor describes a game; it does not contain one.",
                ]);
        }

        GameDescriptor? descriptor;
        try
        {
            descriptor = _deserializer.Deserialize<GameDescriptor>(File.ReadAllText(descriptorPath));
        }
        catch (YamlException exception)
        {
            throw new GameDescriptorValidationException(
                descriptorPath,
                [
                    $"{descriptorPath}: yaml: expected a valid game descriptor document, but the file could not be "
                    + $"read as one - {DescribeYamlFailure(exception)}.",
                ]);
        }

        IReadOnlyList<string> problems =
            GameDescriptorValidator.Validate(descriptor, descriptorPath, Path.GetFileNameWithoutExtension(descriptorPath));

        if (problems.Count > 0)
        {
            throw new GameDescriptorValidationException(descriptorPath, problems);
        }

        // Validate() reports an empty document as a problem, so reaching here means descriptor is set.
        return descriptor!;
    }

    /// <inheritdoc/>
    public GameDescriptor? LoadById(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        foreach ((string candidateId, string file) in EnumerateDescriptors())
        {
            if (string.Equals(candidateId, id, StringComparison.Ordinal))
            {
                return LoadFromFile(file);
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public IReadOnlyList<GameDescriptor> LoadAll()
    {
        List<GameDescriptor> descriptors = [];

        foreach ((_, string file) in EnumerateDescriptors())
        {
            descriptors.Add(LoadFromFile(file));
        }

        return descriptors;
    }

    /// <summary>
    /// Lists every <c>&lt;id&gt;.yaml</c> in <see cref="DescriptorDirectories"/>, in a deterministic
    /// order, refusing two files that claim the same id.
    /// </summary>
    private IReadOnlyList<(string Id, string Path)> EnumerateDescriptors()
    {
        List<(string Id, string Path)> descriptors = [];
        Dictionary<string, string> idToFile = new(StringComparer.OrdinalIgnoreCase);

        foreach (string directory in _directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(directory, "*" + DescriptorExtension).Order(StringComparer.Ordinal))
            {
                string id = Path.GetFileNameWithoutExtension(file);

                if (idToFile.TryGetValue(id, out string? existing))
                {
                    throw new GameDescriptorValidationException(
                        file,
                        [
                            $"{file}: id: expected a game id that is unique across descriptor directories, but '{id}' "
                            + $"is already declared by '{existing}'.",
                        ]);
                }

                idToFile[id] = file;
                descriptors.Add((id, file));
            }
        }

        return descriptors;
    }

    /// <summary>Builds one line from a YAML failure, preferring the innermost message.</summary>
    /// <remarks>
    /// YamlDotNet wraps the useful message ("The value ... is not a valid YAML Boolean") inside a
    /// generic outer exception ("Exception during deserialization"), and puts the position on the
    /// outer one. Both are needed for a message an author can act on.
    /// </remarks>
    private static string DescribeYamlFailure(YamlException exception)
    {
        Exception innermost = exception;
        while (innermost.InnerException is not null)
        {
            innermost = innermost.InnerException;
        }

        bool hasPosition = exception.Start.Line > 0;
        string position = hasPosition ? $"line {exception.Start.Line}, column {exception.Start.Column}: " : string.Empty;

        return $"{position}{innermost.Message}";
    }

    /// <summary>
    /// Maps <c>IReadOnlyList&lt;T&gt;</c> properties, which YamlDotNet 18.1.0 cannot build on its own
    /// ("No node deserializer was able to deserialize the node into type
    /// System.Collections.Generic.IReadOnlyList"). The list is built as a <c>List&lt;T&gt;</c>, which
    /// satisfies the property type.
    /// </summary>
    private sealed class ReadOnlyListYamlConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) =>
            type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            Type elementType = type.GetGenericArguments()[0];

            return rootDeserializer(typeof(List<>).MakeGenericType(elementType));
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
            throw new NotSupportedException("Game descriptors are read, never written.");
    }
}
