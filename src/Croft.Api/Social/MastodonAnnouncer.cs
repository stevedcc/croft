using System.Text;
using System.Text.RegularExpressions;
using Croft.Shared.Contracts.Social;
using Microsoft.Extensions.Options;

namespace Croft.Api.Social;

/// <summary>
/// Announces on the owner's Mastodon account (brief 12d): <c>POST /api/v1/statuses</c> with the access token and the outbox row's id as
/// <c>Idempotency-Key</c> (the server answers a repeated key with the first status instead of posting again), and the look for an earlier send through
/// the account's latest public statuses. Mastodon builds the link card and finds the hashtags itself.
/// </summary>
public sealed class MastodonAnnouncer(IHttpClientFactory clients, IOptions<SocialOptions> options, TimeProvider time) : ISocialAnnouncer
{
    private const string Network = "Mastodon";

    public SocialPlatform Platform => SocialPlatform.Mastodon;

    public async Task<AnnounceOutcome> SendAsync(AnnouncementMessage message, CancellationToken ct)
    {
        var settings = options.Value;
        var body = new Dictionary<string, string> { ["status"] = message.Text, ["visibility"] = "public" };
        if (!string.IsNullOrWhiteSpace(settings.AnnouncementLanguage))
            body["language"] = settings.AnnouncementLanguage.Trim();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{settings.Mastodon.Server.Trim()}/api/v1/statuses")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.Mastodon.AccessToken.Trim());
        request.Headers.Add("Idempotency-Key", message.RowId.ToString("D"));

        SocialResponse response;
        try
        {
            response = await SocialHttp.SendAsync(clients.CreateClient(SocialHttp.Announce), request, SocialHttp.ThreadLimit, SocialHttp.AnnounceTimeout, keepErrorBody: true, ct);
        }
        catch (Exception ex) when (AnnounceErrors.IsExpected(ex, ct))
        {
            return new AnnounceOutcome.Retry(SocialFetchErrors.Reason(Network, ex), null);
        }

        if (!response.IsSuccess)
        {
            return AnnounceErrors.Classify(Network, "post", response.Status, response.Body,
                () => SocialRateLimit.RetryAt(response.Headers, Now()), settings.Mastodon.AccessToken.Trim());
        }

        // A success without a usable answer may still have posted: the retry's look for an earlier send settles it.
        return ReadStatus(response.Body) is { } posted ? posted : new AnnounceOutcome.Retry("Mastodon sent an unexpected answer", null);
    }

    public async Task<AnnounceOutcome> FindEarlierAsync(AnnouncementMessage message, CancellationToken ct)
    {
        var settings = options.Value;
        var account = settings.Mastodon.Account.Trim().TrimStart('@');

        // Without the account's name there is nothing to look at; the idempotency key still protects a repeat within Mastodon's window.
        if (account.Length == 0)
            return new AnnounceOutcome.NotFound();

        var server = $"https://{settings.Mastodon.Server.Trim()}";
        try
        {
            var lookup = await GetAsync($"{server}/api/v1/accounts/lookup?acct={Uri.EscapeDataString(account)}", ct);
            if (!lookup.IsSuccess)
                return AnnounceErrors.Classify(Network, "account lookup", lookup.Status, lookup.Body, () => SocialRateLimit.RetryAt(lookup.Headers, Now()));

            string? accountId;
            try
            {
                using var document = SocialJson.Parse(Encoding.UTF8.GetString(lookup.Body), "account lookup");
                accountId = document.RootElement.Str("id");
            }
            catch (SocialParseException)
            {
                accountId = null;
            }

            if (string.IsNullOrEmpty(accountId) || !accountId.All(char.IsAsciiDigit))
                return new AnnounceOutcome.Retry("Mastodon sent an unexpected answer", null);

            var statuses = await GetAsync($"{server}/api/v1/accounts/{accountId}/statuses?limit=20&exclude_replies=true&exclude_reblogs=true", ct);
            if (!statuses.IsSuccess)
                return AnnounceErrors.Classify(Network, "status list", statuses.Status, statuses.Body, () => SocialRateLimit.RetryAt(statuses.Headers, Now()));

            return FindPost(statuses.Body, message.PostUrl) ?? (AnnounceOutcome)new AnnounceOutcome.NotFound();
        }
        catch (Exception ex) when (AnnounceErrors.IsExpected(ex, ct))
        {
            return new AnnounceOutcome.Retry(SocialFetchErrors.Reason(Network, ex), null);
        }
    }

    private Task<SocialResponse> GetAsync(string url, CancellationToken ct) =>
        SocialHttp.GetAsync(clients.CreateClient(SocialHttp.Mastodon), url, SocialHttp.ThreadLimit, ct);

    private DateTime Now() => time.GetUtcNow().UtcDateTime;

    private static AnnounceOutcome.Posted? ReadStatus(byte[] body)
    {
        try
        {
            using var document = SocialJson.Parse(Encoding.UTF8.GetString(body), "status");
            return StatusOf(document.RootElement);
        }
        catch (SocialParseException)
        {
            return null;
        }
    }

    private static AnnounceOutcome.Posted? StatusOf(System.Text.Json.JsonElement status)
    {
        var id = status.Str("id");
        var url = SocialUrl.SafeHttps(status.Str("url")) ?? SocialUrl.SafeHttps(status.Str("uri"));
        return string.IsNullOrEmpty(id) || url is null ? null : new AnnounceOutcome.Posted(id, url);
    }

    /// <summary>The account's status whose text links to the post (the address, not merely a longer address that starts with it) or whose link card is the post.</summary>
    private static AnnounceOutcome.Posted? FindPost(byte[] body, string postUrl)
    {
        System.Text.Json.JsonDocument document;
        try
        {
            document = SocialJson.Parse(Encoding.UTF8.GetString(body), "status list");
        }
        catch (SocialParseException)
        {
            return null;
        }

        using (document)
        {
            var link = AddressPattern(postUrl);
            foreach (var status in document.RootElement.Items())
            {
                var linked = status.Str("content") is { } content && link.IsMatch(content);
                var carded = string.Equals(status.Prop("card").Str("url"), postUrl, StringComparison.Ordinal);
                if ((linked || carded) && StatusOf(status) is { } found)
                    return found;
            }
        }

        return null;
    }

    /// <summary>The address, not followed by a character that would make it another address (a longer slug).</summary>
    private static Regex AddressPattern(string postUrl) =>
        new(Regex.Escape(postUrl) + @"(?![\w\-%])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}
