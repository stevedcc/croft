using System.Text;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;

namespace Croft.Web.E2E;

/// <summary>
/// Shared plumbing for the brief-09f tests of the static reading pages: fixtures published
/// through the API as the mock admin (never through the browser UI), a phone context with touch,
/// and a long post body. Each test class keeps its own run-keyed static fixtures; a fixture class
/// runs on one worker and therefore on one stack instance (WebTest.StackUrls).
/// </summary>
public abstract class StaticReadingTest : WebTest
{
    protected const string Sentence =
        "when I opened the solution again after three years the first thing I noticed was how much " +
        "state lived in places it had no business being";

    /// <summary>One published item: its id and slug.</summary>
    protected sealed record Published(Guid Id, string Slug);

    /// <summary>Creates and publishes a post (or page) as the mock admin.</summary>
    protected async Task<Published> PublishAsync(
        string title, string body, string[]? tags = null, Guid? categoryId = null, PostKind kind = PostKind.Post,
        Guid? bannerImageId = null, NavigationPlacement placement = NavigationPlacement.None, int navigationOrder = 0)
    {
        using var client = MockIdpApi.CreateClient();
        var token = await MockIdpApi.GetAdminAccessTokenAsync(client);
        var created = await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/posts",
            new CreatePostRequest(title, body, Tags: tags, CategoryId: categoryId, Kind: kind,
                BannerImageId: bannerImageId, NavigationPlacement: placement, NavigationOrder: navigationOrder), token);
        var id = created.RootElement.GetProperty("id").GetString()!;
        var published = await MockIdpApi.PostJsonAsync(client, ApiUrl, $"/api/posts/{id}/publish", "{}", token);
        return new Published(Guid.Parse(id), published.RootElement.GetProperty("slug").GetString()!);
    }

    /// <summary>Uploads a dark 1600x900 PNG as the mock admin and returns the media id: a post's
    /// banner. A blank page's screenshot is that image, so no image library is needed.</summary>
    protected async Task<Guid> UploadBannerAsync(string fileName)
    {
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = 1600, Height = 900 }
        });
        var page = await context.NewPageAsync();
        await page.SetContentAsync("<style>html, body { margin: 0; background: #1B2A3A; }</style>");
        var png = await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Png });
        using var client = MockIdpApi.CreateClient();
        var token = await MockIdpApi.GetAdminAccessTokenAsync(client);
        return await MockIdpApi.UploadImageAsync(client, ApiUrl, fileName, png, token);
    }

    /// <summary>Creates a category as the mock admin; returns its id and slug.</summary>
    protected async Task<(Guid Id, string Slug)> CreateCategoryAsync(string name)
    {
        using var client = MockIdpApi.CreateClient();
        var token = await MockIdpApi.GetAdminAccessTokenAsync(client);
        var created = await MockIdpApi.PostJsonAsync(client, ApiUrl, "/api/categories",
            System.Text.Json.JsonSerializer.Serialize(name), token);
        return (Guid.Parse(created.RootElement.GetProperty("id").GetString()!),
            created.RootElement.GetProperty("slug").GetString()!);
    }

    /// <summary>The brief's phone: 390×844 with touch.</summary>
    protected async Task<IBrowserContext> NewPhoneContextAsync() =>
        await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = 390, Height = 844 },
            HasTouch = true
        });

    /// <summary>The brief's desktop: 1440×900.</summary>
    protected async Task<IBrowserContext> NewDesktopContextAsync() =>
        await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = 1440, Height = 900 }
        });

    /// <summary>An intro, then N h2 sections of <paramref name="repeats"/> × 26 words each (long
    /// enough to scroll and for the scroll spy to have sections to tell apart), a C# code block
    /// in section 1 and optional extra text appended to the intro.</summary>
    protected static string BodyWithSections(string runId, int sections, int repeats = 10, string intro = "")
    {
        var body = new StringBuilder();
        body.Append($"Fixture intro {runId} with enough words to start the page comfortably. {intro}\n\n");
        for (var n = 1; n <= sections; n++)
        {
            body.Append($"## Section {n}\n\n");
            if (n == 1)
            {
                body.Append("```csharp\nvar answer = 42;\nConsole.WriteLine(answer);\n```\n\n");
            }

            for (var repeat = 0; repeat < repeats; repeat++)
            {
                body.Append(Sentence).Append(". ");
            }

            body.Append("\n\n");
        }

        return body.ToString();
    }
}
