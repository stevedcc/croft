using AwesomeAssertions;
using Croft.Api.Social;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Croft.Api.Tests.Social;

/// <summary>Brief 12a: the <c>Social</c> settings: off by default, named by the environment-variable form the k8s map uses, and a platform with no server or DID is disabled.</summary>
public class SocialSettingsTests
{
    private static SocialOptions Bind(Dictionary<string, string?> values)
    {
        var options = new SocialOptions();
        new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection(SocialOptions.SectionName).Bind(options);
        return options;
    }

    [Fact]
    public void WithNothingSet_SocialIsOff_AndNamesNobody()
    {
        var options = Bind([]);

        options.Enabled.Should().BeFalse("nothing is fetched and no section shows until the owner switches it on");
        options.Mastodon.Server.Should().BeEmpty();
        options.Mastodon.Account.Should().BeEmpty();
        options.Bluesky.Did.Should().BeEmpty();
        options.MastodonConfigured.Should().BeFalse();
        options.BlueskyConfigured.Should().BeFalse();
    }

    [Fact]
    public void TheSettings_BindFromTheirKeys()
    {
        // The k8s map says Social__Mastodon__Server; the environment-variable provider turns that into Social:Mastodon:Server.
        var options = Bind(new()
        {
            ["Social:Enabled"] = "true",
            ["Social:Mastodon:Server"] = "mastodon.example",
            ["Social:Mastodon:Account"] = "owner",
            ["Social:Bluesky:Did"] = "did:plc:x",
        });

        options.Enabled.Should().BeTrue();
        options.Mastodon.Server.Should().Be("mastodon.example");
        options.Mastodon.Account.Should().Be("owner");
        options.Bluesky.Did.Should().Be("did:plc:x");
        options.MastodonConfigured.Should().BeTrue();
        options.BlueskyConfigured.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "did:plc:x", false, true)]
    [InlineData("mastodon.example", "", true, false)]
    [InlineData("  ", "  ", false, false)]
    public void EnabledWithAnEmptyServerOrDid_DisablesThatPlatformOnly(string server, string did, bool mastodon, bool bluesky)
    {
        var options = Bind(new() { ["Social:Enabled"] = "true", ["Social:Mastodon:Server"] = server, ["Social:Bluesky:Did"] = did });

        options.MastodonConfigured.Should().Be(mastodon);
        options.BlueskyConfigured.Should().Be(bluesky);
    }

    // --- brief 12d: announcing ---

    [Fact]
    public void WithNothingSet_NothingCanAnnounce_AndBlueskyUsesItsPublicService()
    {
        var options = Bind([]);

        options.Mastodon.AccessToken.Should().BeEmpty();
        options.Bluesky.AppPassword.Should().BeEmpty();
        options.Bluesky.Service.Should().Be("https://bsky.social");
        options.MastodonCanAnnounce.Should().BeFalse();
        options.BlueskyCanAnnounce.Should().BeFalse();
    }

    [Fact]
    public void AnnouncementLanguage_DefaultsToEnglish_AndBindsFromItsKey_AndBlankMeansNone()
    {
        Bind([]).AnnouncementLanguage.Should().Be("en");
        Bind(new() { ["Social:AnnouncementLanguage"] = "de" }).AnnouncementLanguage.Should().Be("de");
        Bind(new() { ["Social:AnnouncementLanguage"] = "" }).AnnouncementLanguage.Should().BeEmpty("blank = the announcement carries no language");
    }

    [Fact]
    public void TheAnnouncingSettings_BindFromTheirKeys()
    {
        var options = Bind(new()
        {
            ["Social:Mastodon:AccessToken"] = "test-token-0f3a",
            ["Social:Bluesky:AppPassword"] = "test-pass-77c1",
            ["Social:Bluesky:Service"] = "https://pds.example",
        });

        options.Mastodon.AccessToken.Should().Be("test-token-0f3a");
        options.Bluesky.AppPassword.Should().Be("test-pass-77c1");
        options.Bluesky.Service.Should().Be("https://pds.example");
    }

    [Theory]
    // enabled, server, token, base url -> mastodon ; enabled, did, password, base url -> bluesky
    [InlineData(true, "mastodon.example", "tok", "https://blog.example", true)]
    [InlineData(false, "mastodon.example", "tok", "https://blog.example", false)]
    [InlineData(true, "", "tok", "https://blog.example", false)]
    [InlineData(true, "mastodon.example", "", "https://blog.example", false)]
    [InlineData(true, "mastodon.example", "  ", "https://blog.example", false)]
    [InlineData(true, "mastodon.example", "tok", "", false)]
    [InlineData(true, "mastodon.example", "tok", null, false)]
    public void Mastodon_IsReady_OnlyWithEverythingSet(bool enabled, string server, string token, string? baseUrl, bool ready)
    {
        var options = new SocialOptions
        {
            Enabled = enabled,
            BaseUrl = baseUrl ?? string.Empty,
            Mastodon = new MastodonSocialOptions { Server = server, AccessToken = token },
        };

        options.MastodonCanAnnounce.Should().Be(ready);
    }

    [Theory]
    [InlineData(true, "did:plc:x", "pw", "https://blog.example", true)]
    [InlineData(false, "did:plc:x", "pw", "https://blog.example", false)]
    [InlineData(true, "", "pw", "https://blog.example", false)]
    [InlineData(true, "did:plc:x", "", "https://blog.example", false)]
    [InlineData(true, "did:plc:x", "pw", "", false)]
    public void Bluesky_IsReady_OnlyWithEverythingSet(bool enabled, string did, string password, string baseUrl, bool ready)
    {
        var options = new SocialOptions
        {
            Enabled = enabled,
            BaseUrl = baseUrl,
            Bluesky = new BlueskySocialOptions { Did = did, AppPassword = password },
        };

        options.BlueskyCanAnnounce.Should().Be(ready);
    }

    [Fact]
    public async Task TheApi_TakesTheBaseUrl_FromBlogBaseUrl()
    {
        await using var factory = new Fixtures.BlogApiFactory("Host=unused", Path.GetTempPath(),
            new Dictionary<string, string?> { ["Blog:BaseUrl"] = "https://blog.example/" });

        factory.Services.GetRequiredService<IOptions<SocialOptions>>().Value.BaseUrl.Should().Be("https://blog.example/");
    }
}
