namespace GamePolish.Core.IO;

/// <summary>
/// The mutating-call seam: deliberately <b>only</b> the calls that change the filesystem.
/// </summary>
/// <remarks>
/// <para>
/// Narrow on purpose. Task 205 needs a class that can prove the order of its own mutations - the
/// snapshot directory has to exist before the first byte of mod content moves (invariant #1) - and the
/// install pipeline (208-210) needs to inject a failure at an exact point. Read calls, <c>File.Exists</c>
/// and <c>Path</c> are deliberately <i>not</i> abstracted: a wide file-system abstraction is scope creep
/// that makes every call site harder to read without making anything safer.
/// </para>
/// <para>
/// This is the second thing in Core that writes to disk, and on purpose: <c>AtomicJson</c> writes the
/// app's own state files under the data root, and this writes game content into a game's install
/// directory, which only the install pipeline may do. Nothing else in Core should write.
/// </para>
/// </remarks>
public interface IFileOps
{
    /// <summary>Creates a directory, including any missing parents.</summary>
    void CreateDirectory(string path);

    /// <summary>Copies a file, creating the destination's parent directory when it is missing.</summary>
    void CopyFile(string source, string destination, bool overwrite);

    /// <summary>Moves a directory to a destination that must not exist.</summary>
    void MoveDirectory(string source, string destination);

    /// <summary>Deletes a directory, optionally with its contents.</summary>
    void DeleteDirectory(string path, bool recursive);

    /// <summary>Deletes a file. A missing file is not an error, matching <c>File.Delete</c>.</summary>
    void DeleteFile(string path);

    /// <summary>Writes bytes to a file, creating the parent directory when it is missing.</summary>
    void WriteAllBytes(string path, byte[] contents);
}
