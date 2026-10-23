using AwesomeAssertions;
using Croft.Shared.Contracts;
using Croft.Web.Services;

namespace Croft.Web.Tests;

public class PersistentStateBudgetTests
{
    [Fact]
    public void ALongPostOfEuroSigns_DoesNotFit_AndAnOrdinaryPostDoes()
    {
        PersistentStateBudget.Fits(new string('a', 20_000)).Should().BeTrue("an ordinary post travels with the page");
        PersistentStateBudget.Fits(new string('€', 200_000)).Should().BeFalse(
            "200,000 € signs are 1.2 MB of JSON, 12 MB encoded: the circuit that measured it never connected");
    }

    [Fact]
    public void TheBudget_IsMeasuredOnTheJsonThePageStateUses()
    {
        // The escaped form is what is counted: each € is the six characters €.
        PersistentStateBudget.Fits(new string('€', PersistentStateBudget.MaxJsonBytes / 6 - 10)).Should().BeTrue();
        PersistentStateBudget.Fits(new string('€', PersistentStateBudget.MaxJsonBytes / 6 + 10)).Should().BeFalse();
    }

    [Fact]
    public void FeedbackListsCount_TowardsTheSameBudget()
    {
        var note = new FeedbackDto(Guid.NewGuid(), Guid.NewGuid(), "a", "A", new string('q', 2_000), new string('c', 10_000), DateTime.UtcNow, null);

        PersistentStateBudget.Fits(Enumerable.Repeat(note, 5).ToList()).Should().BeTrue();
        PersistentStateBudget.Fits(Enumerable.Repeat(note, 20).ToList()).Should().BeFalse();
    }
}
