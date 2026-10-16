namespace GamePolish.Core.Tests.Storage;

/// <summary>
/// Sets an environment variable for the lifetime of a <c>using</c> block and restores the previous
/// value afterwards.
/// </summary>
internal sealed class TemporaryEnvironmentVariable : IDisposable
{
    private readonly string _name;
    private readonly string? _previousValue;

    private TemporaryEnvironmentVariable(string name, string? value)
    {
        _name = name;
        _previousValue = Environment.GetEnvironmentVariable(name);

        Environment.SetEnvironmentVariable(name, value);
    }

    /// <summary>Sets <paramref name="name"/> to <paramref name="value"/>; null removes it.</summary>
    /// <param name="name">Variable name.</param>
    /// <param name="value">Value, or null to delete the variable.</param>
    public static TemporaryEnvironmentVariable Set(string name, string? value) => new(name, value);

    /// <inheritdoc/>
    public void Dispose() => Environment.SetEnvironmentVariable(_name, _previousValue);
}
