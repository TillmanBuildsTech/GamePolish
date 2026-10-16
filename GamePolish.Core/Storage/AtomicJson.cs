using System.Text.Json;
using System.Text.Json.Serialization;

namespace GamePolish.Core.Storage;

/// <summary>
/// The one JSON reader and writer for every file this app owns under the data root:
/// <c>app-settings.json</c>, <c>games.json</c>, <c>manifest.json</c>,
/// <c>registry-cache.json</c> and the backup index.
/// </summary>
/// <remarks>
/// <para>
/// Writes are atomic. The payload goes to a uniquely named sibling temporary file in the same
/// directory as the target, then moves over the target with
/// <see cref="File.Move(string, string, bool)"/>. A reader therefore never observes a half-written
/// file, and a failed write leaves the previous contents intact. The temporary file is a sibling on
/// purpose: a rename across volumes is not atomic.
/// </para>
/// <para>
/// Reads are deliberately thin. <see cref="Read{T}"/> returns <c>default</c> when the file is
/// absent and lets <see cref="JsonException"/> propagate when it is malformed, because only the
/// caller knows whether a corrupt file is recoverable: an empty <c>app-settings.json</c> is rebuilt
/// from defaults, while a corrupt <c>games.json</c> records where the user's games live and must
/// never be silently replaced.
/// </para>
/// </remarks>
public static class AtomicJson
{
    /// <summary>Serializer options shared by every read and write in the app.</summary>
    /// <remarks>
    /// Property names are <c>snake_case</c> so the files on disk match the design notes'
    /// <c>schema_version</c> / <c>install_path</c> spelling; nulls are omitted, so an unset optional
    /// setting is absent from the file rather than written as <c>null</c>.
    /// </remarks>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Reads <paramref name="path"/>, or returns <c>default</c> when it does not exist.</summary>
    /// <typeparam name="T">The type serialized into the file.</typeparam>
    /// <param name="path">Absolute path of the file.</param>
    /// <exception cref="JsonException">The file exists but is not valid JSON for <typeparamref name="T"/>.</exception>
    public static T? Read<T>(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
    }

    /// <summary>Writes <paramref name="value"/> to <paramref name="path"/>, atomically.</summary>
    /// <typeparam name="T">The type to serialize.</typeparam>
    /// <param name="path">Absolute path of the target file.</param>
    /// <param name="value">The value to serialize.</param>
    /// <remarks>
    /// The containing directory is created when missing, so a caller cannot lose a write to a
    /// missing directory after it has already accepted the user's intent to save.
    /// </remarks>
    public static void Write<T>(string path, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string targetPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(value, Options));
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            // On success the move already consumed the temp file; on failure this is the only copy of
            // the payload and it must not be left behind.
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
