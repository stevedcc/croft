namespace Croft.Api.Social;

/// <summary>
/// The <c>Social</c> settings (brief 12a): off by default, so a site that sets nothing fetches nothing and shows no
/// discussion. Enabled with an empty server or DID disables that platform only.
/// </summary>
public sealed class SocialOptions
{
    public const string SectionName = "Social";

    /// <summary>Fetching and showing replies; the link fields validate and save either way (the owner can prepare posts).</summary>
    public bool Enabled { get; set; }

    /// <summary>The language (BCP-47) an announcement is sent in: Mastodon's <c>language</c>, Bluesky's <c>langs</c>. Blank = none is sent.</summary>
    public string AnnouncementLanguage { get; set; } = "en";

    public MastodonSocialOptions Mastodon { get; set; } = new();

    public BlueskySocialOptions Bluesky { get; set; } = new();

    public bool MastodonConfigured => !string.IsNullOrWhiteSpace(Mastodon.Server);

    public bool BlueskyConfigured => !string.IsNullOrWhiteSpace(Bluesky.Did);

    /// <summary>The site's address (<c>Blog:BaseUrl</c>, not a <c>Social:</c> key; the program fills it): what an announcement's post address starts with.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Announcing on Mastodon (brief 12d): social is on, and the server, the access token and the site's address are all set. Otherwise nothing is queued or sent for it.</summary>
    public bool MastodonCanAnnounce => Enabled && MastodonConfigured && !string.IsNullOrWhiteSpace(Mastodon.AccessToken) && !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>Announcing on Bluesky (brief 12d): social is on, and the DID, the app password and the site's address are all set.</summary>
    public bool BlueskyCanAnnounce => Enabled && BlueskyConfigured && !string.IsNullOrWhiteSpace(Bluesky.AppPassword) && !string.IsNullOrWhiteSpace(BaseUrl);
}

public sealed class MastodonSocialOptions
{
    /// <summary>The owner's server host, e.g. <c>mastodon.example</c>: the only host a linked post may be on.</summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>The owner's account name on that server, without the @: the only account a linked post may belong to.</summary>
    public string Account { get; set; } = string.Empty;

    /// <summary>A secret (from <c>blog-api-secrets</c>): an access token of the owner's account with the <c>write:statuses</c> scope only. Empty = no announcing.</summary>
    public string AccessToken { get; set; } = string.Empty;
}

public sealed class BlueskySocialOptions
{
    /// <summary>The owner's DID: the only author a linked post may have.</summary>
    public string Did { get; set; } = string.Empty;

    /// <summary>A secret (from <c>blog-api-secrets</c>): an app password without access to direct messages. Empty = no announcing.</summary>
    public string AppPassword { get; set; } = string.Empty;

    /// <summary>The PDS the announcing talks to (<c>createSession</c>, <c>uploadBlob</c>, <c>createRecord</c>).</summary>
    public string Service { get; set; } = "https://bsky.social";
}
