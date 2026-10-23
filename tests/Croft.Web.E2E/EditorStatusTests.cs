using System.Text.RegularExpressions;
using Croft.Shared.Contracts;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 13g item 6 (review I-4, M-8): the editor's status changes are written into one live region
/// (so a screen reader hears them), and Enter in the tag field adds the tag without saving anything.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class EditorStatusTests : StaticReadingTest
{
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    [Test]
    public async Task StatusRegion_SaysSaved_Published_AndUnpublished_AfterEachButton()
    {
        using var client = MockIdpApi.CreateClient();
        var token = await MockIdpApi.GetAdminAccessTokenAsync(client);
        var draft = await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/posts",
            new CreatePostRequest($"Status {RunId}", "Body."), token);
        var id = draft.RootElement.GetProperty("id").GetString();

        await LoginAsync("admin@example.com", "password");
        await Page.GotoAsync($"{BaseUrl}/admin/posts/{id}/edit");
        await WaitForInteractiveAsync(Page);
        var region = Page.Locator("[role=status].post-actions__saved");
        await Expect(region).ToHaveCountAsync(1);
        await Expect(region).ToHaveTextAsync(string.Empty);

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save draft" }).ClickAsync();
        await Expect(region).ToHaveTextAsync(new Regex(@"^Saved \d\d:\d\d$"));

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
        await Expect(region).ToHaveTextAsync("Published");

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Unpublish" }).ClickAsync();
        await Expect(region).ToHaveTextAsync("Unpublished");
    }

    [Test]
    public async Task EnterInTheTagField_AddsAPill_AndSavesNothing()
    {
        await LoginAsync("admin@example.com", "password");
        await OpenNewPostEditorAsync();
        var entry = Page.GetByLabel("Tags");
        await entry.FillAsync($"enter{RunId}");
        await entry.PressAsync("Enter");

        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Remove tag enter{RunId}" })).ToBeVisibleAsync();
        await Expect(entry).ToHaveValueAsync(string.Empty);
        // A save would announce itself and move the browser to the post's edit route.
        await Expect(Page.Locator("[role=status].post-actions__saved")).ToHaveTextAsync(string.Empty);
        Assert.That(Page.Url, Does.EndWith("/admin/posts/new"), "Enter added the tag; it did not save the post");
    }

    [Test]
    public async Task ScheduleButton_WhenDisabled_IsDescribedByVisibleText()
    {
        await LoginAsync("admin@example.com", "password");
        await OpenNewPostEditorAsync();

        var schedule = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Schedule" });
        await Expect(schedule).ToBeDisabledAsync();
        await Expect(schedule).ToHaveAccessibleDescriptionAsync("Schedule needs a publish date and time in the future.");
        await Expect(Page.Locator(".post-actions__hint")).ToBeVisibleAsync();
    }
}
