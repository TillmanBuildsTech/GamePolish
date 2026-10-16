namespace GamePolish.Core.Storage;

/// <summary>Loads and saves <see cref="AppSettings"/>.</summary>
public interface IAppSettingsStore
{
    /// <summary>
    /// Returns the stored preferences, or the defaults when the file does not exist. Never throws on
    /// absence and never creates the file.
    /// </summary>
    /// <exception cref="System.Text.Json.JsonException">
    /// The file exists but is not valid JSON. The caller decides how to recover; the file is left as
    /// it was found.
    /// </exception>
    AppSettings Load();

    /// <summary>
    /// Writes <paramref name="settings"/> atomically. The caller owns
    /// <see cref="AppSettings.SchemaVersion"/>: this method bumps nothing.
    /// </summary>
    void Save(AppSettings settings);
}

/// <summary>
/// The <see cref="AppSettings"/> store: one JSON file under the data root, written through
/// <see cref="AtomicJson"/> so it can never be observed half-written.
/// </summary>
public sealed class AppSettingsStore : IAppSettingsStore
{
    private readonly GamePolishPaths _paths;

    /// <summary>Initializes a store over <paramref name="paths"/>.</summary>
    /// <param name="paths">The resolved data root.</param>
    public AppSettingsStore(GamePolishPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
    }

    /// <summary>Initializes a store over the resolved data root.</summary>
    /// <remarks>
    /// Convenience for callers that have no root of their own (the desktop app). Tests and the CLI
    /// pass a root explicitly so nothing depends on ambient state.
    /// </remarks>
    public AppSettingsStore()
        : this(GamePolishPaths.Resolve())
    {
    }

    /// <inheritdoc/>
    public AppSettings Load() => AtomicJson.Read<AppSettings>(_paths.AppSettingsFile) ?? new AppSettings();

    /// <inheritdoc/>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _paths.EnsureCreated();

        AtomicJson.Write(_paths.AppSettingsFile, settings);
    }
}
