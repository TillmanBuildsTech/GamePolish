using GamePolish.Core.Operations;

namespace GamePolish.Core.Tests.Operations;

/// <summary>
/// Covers <see cref="OperationProgress"/> (.todo/212): the shape the install pipeline reports while it runs
/// (task 208) and the library view renders (task 302).
/// </summary>
public class OperationProgressTests
{
    [Fact]
    public void CarriesThePhaseTheCountsAndTheCurrentItem()
    {
        OperationProgress progress = new("copying", Completed: 3, Total: 12, CurrentItem: "pfUI/pfUI.toc");

        Assert.Equal("copying", progress.Phase);
        Assert.Equal(3, progress.Completed);
        Assert.Equal(12, progress.Total);
        Assert.Equal("pfUI/pfUI.toc", progress.CurrentItem);
    }

    [Fact]
    public void ATotalOfZeroMeansTheCountIsNotKnownYet()
    {
        OperationProgress progress = new("cloning", Completed: 0, Total: 0);

        Assert.Equal(0, progress.Total);
        Assert.Null(progress.CurrentItem);
    }

    [Fact]
    public void TwoProgressReportsWithTheSameValuesAreEqual()
    {
        // Value equality is what lets a view skip a redundant re-render while a copy is running.
        Assert.Equal(new OperationProgress("copying", 3, 12), new OperationProgress("copying", 3, 12));
        Assert.NotEqual(new OperationProgress("copying", 3, 12), new OperationProgress("copying", 4, 12));
    }
}
