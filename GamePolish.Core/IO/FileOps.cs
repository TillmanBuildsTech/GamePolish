namespace GamePolish.Core.IO;

/// <summary>
/// The real <see cref="IFileOps"/>: forwards to <see cref="File"/> and <see cref="Directory"/>.
/// </summary>
public sealed class FileOps : IFileOps
{
    /// <inheritdoc/>
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <inheritdoc/>
    public void CopyFile(string source, string destination, bool overwrite)
    {
        string? directory = Path.GetDirectoryName(destination);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Copy(source, destination, overwrite);
    }

    /// <inheritdoc/>
    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);

    /// <inheritdoc/>
    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);

    /// <inheritdoc/>
    public void DeleteFile(string path) => File.Delete(path);

    /// <inheritdoc/>
    public void WriteAllBytes(string path, byte[] contents)
    {
        string? directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, contents);
    }
}
