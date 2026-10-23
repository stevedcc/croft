using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Api.Tests.Fixtures;
using Croft.Api.Tests.Fixtures.Social;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Api.Tests.Social;

/// <summary>
/// Brief 12d item 1: a post carries an announcement text and a flag per network. New posts announce by default, pages never; an update's null leaves
/// them alone and an empty text goes back to the default; a text over the limit of a network whose flag is on is refused with the field's name.
/// </summary>
public class AnnouncementSettingsTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string BaseUrl = "https://blog.example";

    private BlogApiFactory Factory() =>
        SocialApiFactory.Create(fixture, configuration: new() { ["Blog:BaseUrl"] = BaseUrl });

    [Fact]
    public async Task NewPost_WithTheFlagsLeftOut_AnnouncesOnBothNetworks()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");

        var created = await CreateAsync(admin, new CreatePostRequest("Defaults post one", "body"));

        created.AnnounceMastodon.Should().BeTrue();
        created.AnnounceBluesky.Should().BeTrue();
        created.AnnouncementText.Should().BeNull();
        var stored = await LoadAsync(factory, created.Id);
        (stored.AnnounceMastodon, stored.AnnounceBluesky, stored.AnnouncementText).Should().Be((true, true, null));
    }

    [Fact]
    public async Task NewPage_WithTheFlagsLeftOut_AnnouncesNothing()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");

        var created = await CreateAsync(admin, new CreatePostRequest("Defaults page one", "body", Kind: PostKind.Page));

        created.AnnounceMastodon.Should().BeFalse();
        created.AnnounceBluesky.Should().BeFalse();
        var stored = await LoadAsync(factory, created.Id);
        (stored.AnnounceMastodon, stored.AnnounceBluesky).Should().Be((false, false));
    }

    [Fact]
    public async Task NewPost_KeepsWhatTheRequestSays()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");

        var created = await CreateAsync(admin, new CreatePostRequest("Explicit post one", "body",
            AnnouncementText: "My own words", AnnounceMastodon: true, AnnounceBluesky: false));

        created.AnnouncementText.Should().Be("My own words");
        created.AnnounceMastodon.Should().BeTrue();
        created.AnnounceBluesky.Should().BeFalse();
    }

    [Theory]
    [InlineData(true, null, null)]
    [InlineData(null, true, null)]
    [InlineData(null, null, "Some words")]
    public async Task APage_CannotAskToBeAnnounced(bool? mastodon, bool? bluesky, string? text)
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");

        var response = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest($"Refused page {Guid.NewGuid():N}", "body",
            Kind: PostKind.Page, AnnounceMastodon: mastodon, AnnounceBluesky: bluesky, AnnouncementText: text));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await DetailAsync(response)).Should().Be("Announcing is not allowed for pages.");
    }

    [Fact]
    public async Task Update_NullLeavesTheTextAndTheFlagsAlone()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");
        var created = await CreateAsync(admin, new CreatePostRequest("Update null one", "body",
            AnnouncementText: "Kept words", AnnounceMastodon: true, AnnounceBluesky: false));

        var updated = await UpdateAsync(admin, created, new UpdatePostRequest("Update null one", "edited"));

        updated.AnnouncementText.Should().Be("Kept words");
        updated.AnnounceMastodon.Should().BeTrue();
        updated.AnnounceBluesky.Should().BeFalse();
    }

    [Fact]
    public async Task Update_AnEmptyText_GoesBackToTheDefault_StoredAsNull()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");
        var created = await CreateAsync(admin, new CreatePostRequest("Update empty one", "body", AnnouncementText: "Own words"));

        var updated = await UpdateAsync(admin, created, new UpdatePostRequest("Update empty one", "body", AnnouncementText: ""));

        updated.AnnouncementText.Should().BeNull();
        (await LoadAsync(factory, created.Id)).AnnouncementText.Should().BeNull("the default is not stored");

        var whitespace = await UpdateAsync(admin, created, new UpdatePostRequest("Update empty one", "body", AnnouncementText: "Again"));
        whitespace.AnnouncementText.Should().Be("Again");
        var cleared = await UpdateAsync(admin, created, new UpdatePostRequest("Update empty one", "body", AnnouncementText: "   "));
        cleared.AnnouncementText.Should().BeNull("a text of white space only is no text");
    }

    [Fact]
    public async Task Update_ASetValueAndFlags_AreStored()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");
        var created = await CreateAsync(admin, new CreatePostRequest("Update value one", "body"));

        var updated = await UpdateAsync(admin, created, new UpdatePostRequest("Update value one", "body",
            AnnouncementText: "New words", AnnounceMastodon: false, AnnounceBluesky: true));

        updated.AnnouncementText.Should().Be("New words");
        updated.AnnounceMastodon.Should().BeFalse();
        updated.AnnounceBluesky.Should().BeTrue();
        var stored = await LoadAsync(factory, created.Id);
        (stored.AnnouncementText, stored.AnnounceMastodon, stored.AnnounceBluesky).Should().Be(("New words", false, true));
    }

    [Fact]
    public async Task Update_OfAPage_WithNullFlags_IsFine_AndWithTrueIsRefused()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");
        var page = await CreateAsync(admin, new CreatePostRequest("Update page one", "body", Kind: PostKind.Page));

        var ok = await UpdateAsync(admin, page, new UpdatePostRequest("Update page one", "edited", Kind: PostKind.Page));
        ok.AnnounceMastodon.Should().BeFalse();

        var refused = await admin.PutAsJsonAsync($"/api/posts/{page.Id}", new UpdatePostRequest("Update page one", "edited", Kind: PostKind.Page, AnnounceBluesky: true));
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await DetailAsync(refused)).Should().Be("Announcing is not allowed for pages.");
    }

    [Fact]
    public async Task Update_ATextOverBlueskysLimit_IsRefused_WithTheExactMessage()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");
        var created = await CreateAsync(admin, new CreatePostRequest("Too long bluesky", "body"));
        var url = $"{BaseUrl}/post/{created.Slug}";
        var body = new string('x', 312 - 2 - url.Length); // the whole announcement is 312 characters

        var response = await admin.PutAsJsonAsync($"/api/posts/{created.Id}", new UpdatePostRequest("Too long bluesky", "body", AnnouncementText: body));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await DetailAsync(response)).Should().Be("AnnouncementText is too long for Bluesky (312 of 300 characters).");
        (await LoadAsync(factory, created.Id)).AnnouncementText.Should().BeNull("nothing is saved when the save is refused");
    }

    [Fact]
    public async Task Update_ATextOverMastodonsLimit_IsRefused_WhenOnlyMastodonIsOn()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");
        var created = await CreateAsync(admin, new CreatePostRequest("Too long mastodon", "body", AnnounceBluesky: false));
        var body = new string('x', 500 - 2 - 23 + 1); // Mastodon counts the address as 23: 501

        var response = await admin.PutAsJsonAsync($"/api/posts/{created.Id}", new UpdatePostRequest("Too long mastodon", "body", AnnouncementText: body));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await DetailAsync(response)).Should().Be("AnnouncementText is too long for Mastodon (501 of 500 characters).");
    }

    [Fact]
    public async Task Update_AtTheMastodonLimit_IsAccepted_BecauseTheAddressCountsAs23_EvenThoughBlueskyIsOff()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");
        var created = await CreateAsync(admin, new CreatePostRequest("Exactly mastodon", "body", AnnounceBluesky: false));
        var body = new string('x', 500 - 2 - 23);

        var updated = await UpdateAsync(admin, created, new UpdatePostRequest("Exactly mastodon", "body", AnnouncementText: body));

        updated.AnnouncementText.Should().Be(body);
    }

    [Fact]
    public async Task Update_ALongTextIsFine_ForANetworkWhoseFlagIsOff()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");
        var created = await CreateAsync(admin, new CreatePostRequest("Bluesky is off", "body", AnnounceBluesky: false));

        var updated = await UpdateAsync(admin, created, new UpdatePostRequest("Bluesky is off", "body", AnnouncementText: new string('x', 400)));

        updated.AnnouncementText.Should().HaveLength(400);
    }

    [Fact]
    public async Task Update_SwitchingANetworkOn_IsRefused_WhenTheStoredTextIsTooLongForIt()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");
        var created = await CreateAsync(admin, new CreatePostRequest("Switch on late", "body", AnnouncementText: new string('x', 400), AnnounceBluesky: false));

        var response = await admin.PutAsJsonAsync($"/api/posts/{created.Id}", new UpdatePostRequest("Switch on late", "body", AnnounceBluesky: true));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await DetailAsync(response)).Should().StartWith("AnnouncementText is too long for Bluesky (");
    }

    [Fact]
    public async Task Create_ATextOverTheLimit_IsRefused_AndNothingIsCreated()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");
        var title = $"Too long on create {Guid.NewGuid():N}";

        var response = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest(title, "body", AnnouncementText: new string('x', 320)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await DetailAsync(response)).Should().MatchRegex(@"^AnnouncementText is too long for Bluesky \(\d+ of 300 characters\)\.$");
        await using var scope = factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<BlogDbContext>().Posts.AnyAsync(p => p.Title == title)).Should().BeFalse();
    }

    [Fact]
    public async Task StaffReads_CarryTheAnnouncementFields_PublicReadsDoNot()
    {
        await using var factory = Factory();
        var admin = factory.CreateClient().WithRole("Admin");
        var created = await CreateAsync(admin, new CreatePostRequest("Visible fields one", "body", AnnouncementText: "Own words"));
        (await admin.PostAsJsonAsync($"/api/posts/{created.Id}/publish", new PublishRequest())).EnsureSuccessStatusCode();

        var staff = (await admin.GetFromJsonAsync<PostDto>($"/api/posts/{created.Slug}"))!;
        var anonymous = (await factory.CreateClient().AsAnonymous().GetFromJsonAsync<PostDto>($"/api/posts/{created.Slug}"))!;

        (staff.AnnouncementText, staff.AnnounceMastodon, staff.AnnounceBluesky).Should().Be(("Own words", true, true));
        (anonymous.AnnouncementText, anonymous.AnnounceMastodon, anonymous.AnnounceBluesky).Should().Be((null, false, false), "an anonymous reader has no use for them");
    }

    private static async Task<PostDto> CreateAsync(HttpClient admin, CreatePostRequest request)
    {
        var response = await admin.PostAsJsonAsync("/api/posts", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PostDto>())!;
    }

    private static async Task<PostDto> UpdateAsync(HttpClient admin, PostDto post, UpdatePostRequest request)
    {
        var response = await admin.PutAsJsonAsync($"/api/posts/{post.Id}", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PostDto>())!;
    }

    private static async Task<string> DetailAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("detail", out var detail) ? detail.GetString() ?? "" : "";
    }

    private static async Task<Post> LoadAsync(BlogApiFactory factory, Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BlogDbContext>().Posts.AsNoTracking().SingleAsync(p => p.Id == id);
    }
}
