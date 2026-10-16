using GamePolish.Core.Storage;

namespace GamePolish.Core.Tests.Storage;

/// <summary>
/// Collection fixture for the storage tests: one per-run temporary data root, exported through
/// <see cref="GamePolishPaths.DataRootEnvironmentVariable"/> so every test in the collection resolves
/// paths the same way the app does, and nothing is ever written near the real
/// <c>~/.game-polish</c>.
/// </summary>
/// <remarks>
/// Environment variables are process-wide, so the tests that read this variable share one xUnit
/// collection and therefore never run concurrently with each other. The default-resolution test
/// clears the variable inside its own scope, which is only safe because of that.
/// </remarks>
public sealed class StorageFixture : IDisposable
{
    private readonly string? _previousDataRoot;
    private readonly string _installRoot;

    public StorageFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "game-polish-core-tests", Guid.NewGuid().ToString("N"));
        _installRoot = Path.Combine(Path.GetTempPath(), "game-polish-core-tests-installs", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Root);

        _previousDataRoot = Environment.GetEnvironmentVariable(GamePolishPaths.DataRootEnvironmentVariable);
        Environment.SetEnvironmentVariable(GamePolishPaths.DataRootEnvironmentVariable, Root);

        Paths = GamePolishPaths.Resolve();
    }

    /// <summary>The temporary data root for this run.</summary>
    public string Root { get; }

    /// <summary>The data root as <see cref="GamePolishPaths.Resolve"/> sees it.</summary>
    public GamePolishPaths Paths { get; }

    /// <summary>Creates and returns a fresh, isolated subdirectory of the data root.</summary>
    /// <param name="name">Directory name, unique per caller.</param>
    public string NewSubdirectory(string name)
    {
        string directory = Path.Combine(Root, name);

        Directory.CreateDirectory(directory);

        return directory;
    }

    /// <summary>
    /// Creates and returns a temporary directory that is deliberately <b>not</b> inside the data root.
    /// </summary>
    /// <param name="name">Directory name, unique per caller.</param>
    /// <remarks>
    /// Game install folders used as fixtures have to live outside the data root: the registry refuses
    /// an install path inside it, because that would collapse the two roots invariant #4 keeps apart.
    /// </remarks>
    public string NewInstallDirectory(string name)
    {
        string directory = Path.Combine(_installRoot, name);

        Directory.CreateDirectory(directory);

        return directory;
    }

    /// <summary>Restores the environment variable and removes the temporary trees.</summary>
    public void Dispose()
    {
        Environment.SetEnvironmentVariable(GamePolishPaths.DataRootEnvironmentVariable, _previousDataRoot);

        TryDelete(Root);
        TryDelete(_installRoot);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover directory under the OS temp path must not fail an otherwise green test run.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}

/// <summary>Names the collection that owns <see cref="StorageFixture"/>.</summary>
[CollectionDefinition(StorageCollection.Name)]
public sealed class StorageCollection : ICollectionFixture<StorageFixture>
{
    /// <summary>Tests in this collection share the temporary data root and never run concurrently.</summary>
    public const string Name = "core-storage";
}
