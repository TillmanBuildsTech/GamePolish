using System.Reflection;

namespace GamePolish.Core.Tests;

/// <summary>
/// Smoke tests proving the test harness runs against a real <c>GamePolish.Core</c> build, plus the
/// one structural invariant that is cheap to guard from day one: Core never gains a UI dependency
/// (.todo/000-CONVENTIONS.md, "Stack, pinned" — pure .NET, no UI references, ever).
/// </summary>
public class CoreSmokeTests
{
    [Fact]
    public void CoreAssemblyIsLoadable()
    {
        var core = Assembly.Load("GamePolish.Core");

        Assert.Equal("GamePolish.Core", core.GetName().Name);
    }

    [Fact]
    public void CoreReferencesNoAvaloniaAssembly()
    {
        var uiReferences = Assembly.Load("GamePolish.Core")
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("Avalonia", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(uiReferences);
    }
}
