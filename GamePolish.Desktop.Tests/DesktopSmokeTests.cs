using System.Reflection;

namespace GamePolish.Desktop.Tests;

/// <summary>
/// Smoke tests proving this project runs under xUnit v3 and that the Avalonia headless packages
/// resolve. The real headless UI harness (an Avalonia test application and <c>[AvaloniaFact]</c>
/// tests) is task 308 — this project is only wired to be capable of it.
/// </summary>
public class DesktopSmokeTests
{
    [Fact]
    public void DesktopAssemblyIsLoadable()
    {
        var desktop = Assembly.Load("GamePolish.Desktop");

        Assert.Equal("GamePolish.Desktop", desktop.GetName().Name);
    }

    [Fact]
    public void AvaloniaHeadlessPackagesAreWired()
    {
        Assert.NotNull(Assembly.Load("Avalonia.Headless.XUnit"));
        Assert.NotNull(Assembly.Load("Avalonia.Headless"));
    }
}
