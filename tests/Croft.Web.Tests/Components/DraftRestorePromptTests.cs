using Croft.Web.Components.Admin;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Croft.Web.Tests.Components;

/// <summary>
/// The restore prompt in the editor surface (D3, owner's decision 2): "You have unsaved changes
/// from &lt;local date and time&gt;. Restore them?" with Restore and Discard buttons on stable
/// selectors. It never restores by itself — every path out of the prompt is a user's click.
/// </summary>
public class DraftRestorePromptTests : BlogBunitContext
{
    [Fact]
    public void Prompt_SaysWhenTheUnsavedChangesAreFrom_AndAsksToRestoreThem()
    {
        var cut = Render<DraftRestorePrompt>(p => p.Add(x => x.FromLocalTime, "10/12/2026, 3:04:05 PM"));

        cut.Find(".draft-restore__text").TextContent.Trim().Should().Be(
            "You have unsaved changes from 10/12/2026, 3:04:05 PM. Restore them?",
            "the prompt names the backup's local date and time and asks the one question");
    }

    [Fact]
    public void Prompt_OffersRestoreAndDiscard_WithStableSelectors()
    {
        var cut = Render<DraftRestorePrompt>(p => p.Add(x => x.FromLocalTime, "10/12/2026, 3:04:05 PM"));

        cut.Find(".draft-restore__restore").TextContent.Trim().Should().Be("Restore");
        cut.Find(".draft-restore__discard").TextContent.Trim().Should().Be("Discard");
    }

    [Fact]
    public async Task Restore_ReportsRestore_Discard_ReportsDiscard()
    {
        var restored = 0;
        var discarded = 0;
        var cut = Render<DraftRestorePrompt>(p => p
            .Add(x => x.FromLocalTime, "10/12/2026, 3:04:05 PM")
            .Add(x => x.OnRestore, EventCallback.Factory.Create(this, () => restored++))
            .Add(x => x.OnDiscard, EventCallback.Factory.Create(this, () => discarded++)));

        cut.Find(".draft-restore__restore").Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() => restored.Should().Be(1, "Restore reports its click"));

        cut.Find(".draft-restore__discard").Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() => discarded.Should().Be(1, "Discard reports its click"));
    }

    [Fact]
    public void Prompt_IsAPassiveStatusBanner_NotAnAlertDialog()
    {
        // Brief 13g item 2 (review M-10): nothing moves focus into the prompt and it does not trap
        // it, so announcing it as an alertdialog promised a dialog it is not. A polite status
        // region is announced without taking focus.
        var cut = Render<DraftRestorePrompt>(p => p.Add(x => x.FromLocalTime, "10/12/2026, 3:04:05 PM"));

        var banner = cut.Find(".draft-restore");
        banner.GetAttribute("role").Should().Be("status");
        banner.GetAttribute("aria-live").Should().Be("polite");
        cut.FindAll("[role=alertdialog]").Should().BeEmpty("the prompt is not a dialog");
    }
}
