using GamePolish.Core.IO;

namespace GamePolish.Core.Tests.Backup;

/// <summary>
/// An <see cref="IFileOps"/> that does the real work and records the order it was asked to do it in.
/// </summary>
/// <remarks>
/// This is what turns "the snapshot directory existed before the first write" into a provable assertion:
/// a test reads <see cref="Calls"/> and fails if a copy ever precedes the directory creation.
/// </remarks>
internal sealed class RecordingFileOps : IFileOps
{
    private readonly IFileOps _inner = new FileOps();

    /// <summary>Every mutating call, in the order it was made.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>Optional failure injection: return an exception to make that copy fail.</summary>
    public Func<string, Exception?>? OnCopyFile { get; init; }

    /// <summary>Optional failure injection: return an exception to make that file creation fail.</summary>
    public Func<string, Exception?>? OnCreateFile { get; init; }

    /// <summary>Optional failure injection: return an exception to make that move fail.</summary>
    public Func<string, string, Exception?>? OnMoveFile { get; init; }

    /// <inheritdoc/>
    public void CreateDirectory(string path)
    {
        Calls.Add($"CreateDirectory({path})");

        _inner.CreateDirectory(path);
    }

    /// <inheritdoc/>
    public void CopyFile(string source, string destination, bool overwrite)
    {
        Calls.Add($"CopyFile({source} => {destination})");

        if (OnCopyFile?.Invoke(source) is { } failure)
        {
            throw failure;
        }

        _inner.CopyFile(source, destination, overwrite);
    }

    /// <inheritdoc/>
    public void MoveDirectory(string source, string destination)
    {
        Calls.Add($"MoveDirectory({source} => {destination})");

        _inner.MoveDirectory(source, destination);
    }

    /// <inheritdoc/>
    public void DeleteDirectory(string path, bool recursive)
    {
        Calls.Add(recursive ? $"DeleteDirectory({path}, recursive)" : $"DeleteDirectory({path})");

        _inner.DeleteDirectory(path, recursive);
    }

    /// <inheritdoc/>
    public void DeleteFile(string path)
    {
        Calls.Add($"DeleteFile({path})");

        _inner.DeleteFile(path);
    }

    /// <inheritdoc/>
    public void WriteAllBytes(string path, byte[] contents)
    {
        Calls.Add($"WriteAllBytes({path})");

        _inner.WriteAllBytes(path, contents);
    }

    /// <inheritdoc/>
    public Stream CreateFile(string path)
    {
        Calls.Add($"CreateFile({path})");

        if (OnCreateFile?.Invoke(path) is { } failure)
        {
            throw failure;
        }

        return _inner.CreateFile(path);
    }

    /// <inheritdoc/>
    public void MoveFile(string source, string destination, bool overwrite)
    {
        Calls.Add($"MoveFile({source} => {destination})");

        if (OnMoveFile?.Invoke(source, destination) is { } failure)
        {
            throw failure;
        }

        _inner.MoveFile(source, destination, overwrite);
    }
}

/// <summary>A clock that only moves when a test moves it.</summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    /// <inheritdoc/>
    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Moves the clock forward.</summary>
    /// <param name="by">How far to move it.</param>
    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
