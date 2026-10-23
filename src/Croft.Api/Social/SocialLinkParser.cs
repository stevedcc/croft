using System.Text.RegularExpressions;
using Croft.Api.Errors;

namespace Croft.Api.Social;

/// <summary>
/// Reads the two post URLs a post may link to (brief 12a). Both are matched exactly: no query string, no fragment, no
/// trailing slash, no port, https only, so only the owner's own posts can carry a discussion.
/// Every failure is a <see cref="ValidationException"/> whose message starts with the field's name.
/// </summary>
public static partial class SocialLinkParser
{
    public const string MastodonField = "MastodonUrl";
    public const string BlueskyField = "BlueskyUrl";

    /// <summary>The status id of <paramref name="url"/>, which must be <c>https://{Server}/@{Account}/{digits}</c> on the configured server and account.</summary>
    public static string ParseMastodon(string url, SocialOptions options)
    {
        if (!options.MastodonConfigured || string.IsNullOrWhiteSpace(options.Mastodon.Account))
            throw new ValidationException($"{MastodonField}: Mastodon is not configured on this site (Social:Mastodon:Server and Social:Mastodon:Account), so no post can be linked.");

        var match = MastodonUrl().Match(url);
        if (!match.Success)
            throw new ValidationException($"{MastodonField} must look like https://{options.Mastodon.Server}/@{options.Mastodon.Account}/<status id>, with nothing after the id.");

        if (!match.Groups["host"].Value.Equals(options.Mastodon.Server.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ValidationException($"{MastodonField} must be a post on {options.Mastodon.Server.Trim()}.");

        if (!match.Groups["account"].Value.Equals(options.Mastodon.Account.Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase))
            throw new ValidationException($"{MastodonField} must be a post of the account @{options.Mastodon.Account.Trim().TrimStart('@')}.");

        return match.Groups["id"].Value;
    }

    /// <summary>The handle-or-DID and record key of <paramref name="url"/>, which must be <c>https://bsky.app/profile/{handle-or-did}/post/{rkey}</c>.</summary>
    public static BlueskyLink ParseBluesky(string url)
    {
        var match = BlueskyUrl().Match(url);
        if (!match.Success)
            throw new ValidationException($"{BlueskyField} must look like https://bsky.app/profile/<handle or DID>/post/<post id>, with nothing after the id.");

        var actor = match.Groups["actor"].Value;
        if (!Did().IsMatch(actor) && !Handle().IsMatch(actor))
            throw new ValidationException($"{BlueskyField}: the profile in the URL is neither a handle nor a DID.");

        return new BlueskyLink(actor, match.Groups["rkey"].Value);
    }

    public static bool IsDid(string actor) => Did().IsMatch(actor);

    /// <summary>The at:// URI of a Bluesky post.</summary>
    public static string BlueskyUri(string did, string rkey) => $"at://{did}/app.bsky.feed.post/{rkey}";

    [GeneratedRegex(@"\Ahttps://(?<host>[A-Za-z0-9.-]+)/@(?<account>[A-Za-z0-9_.-]+)/(?<id>[0-9]{1,32})\z", RegexOptions.CultureInvariant)]
    private static partial Regex MastodonUrl();

    [GeneratedRegex(@"\Ahttps://bsky\.app/profile/(?<actor>[^/?#\s]+)/post/(?<rkey>[A-Za-z0-9._~:-]{1,64})\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BlueskyUrl();

    [GeneratedRegex(@"\Adid:(plc|web):[A-Za-z0-9._:%-]{1,256}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Did();

    [GeneratedRegex(@"\A[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Handle();
}

/// <summary>A Bluesky post link: <c>Actor</c> is a handle or a DID, <c>RecordKey</c> the post's key.</summary>
public sealed record BlueskyLink(string Actor, string RecordKey);
