using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Croft.Shared.Contracts.Social;
using Microsoft.Extensions.Options;

namespace Croft.Api.Social;

/// <summary>
/// Announces on the owner's Bluesky account (brief 12d): <c>createSession</c> with the app password (one session per send, never kept), <c>uploadBlob</c> for the
/// banner fitted by <see cref="SocialImageFitter"/>, <c>createRecord</c> for the post with its link facet, hashtag facets and an external-embed card. Bluesky has no
/// idempotency key: the look for an earlier send (the author feed on the public app view) is what keeps a retry from posting twice.
/// </summary>
public sealed class BlueskyAnnouncer(IHttpClientFactory clients, IOptions<SocialOptions> options, TimeProvider time) : ISocialAnnouncer
{
    private const string Network = "Bluesky";
    private const string PublicAppView = "https://public.api.bsky.app";

    public SocialPlatform Platform => SocialPlatform.Bluesky;

    public async Task<AnnounceOutcome> SendAsync(AnnouncementMessage message, CancellationToken ct)
    {
        var settings = options.Value.Bluesky;
        var password = settings.AppPassword.Trim();
        if (SocialUrl.SafeHttps(settings.Service) is not { } service)
            return new AnnounceOutcome.Failed("Bluesky service address is not valid (Social:Bluesky:Service)");

        service = service.TrimEnd('/');
        try
        {
            // 1. The session.
            var sessionAnswer = await PostAsync(service, "com.atproto.server.createSession",
                JsonContent(new JsonObject { ["identifier"] = settings.Did.Trim(), ["password"] = password }), jwt: null, ct);
            if (!sessionAnswer.IsSuccess)
                return Refused("sign-in", sessionAnswer, password);

            var (jwt, did, handle) = ReadSession(sessionAnswer.Body);
            if (jwt is null || did is null)
                return new AnnounceOutcome.Retry("Bluesky sent an unexpected answer", null);

            // 2. The card's picture, if the post has a banner that can be made to fit.
            JsonNode? thumb = null;
            if (message.LoadBanner is not null && await message.LoadBanner(ct) is { } banner && SocialImageFitter.Fit(banner) is { } jpeg)
            {
                var content = new ByteArrayContent(jpeg);
                content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                var uploadAnswer = await PostAsync(service, "com.atproto.repo.uploadBlob", content, jwt, ct);
                if (!uploadAnswer.IsSuccess)
                    return Refused("image upload", uploadAnswer, password, jwt);

                thumb = ReadBlob(uploadAnswer.Body);
                if (thumb is null)
                    return new AnnounceOutcome.Retry("Bluesky sent an unexpected answer", null);
            }

            // 3. The post.
            var body = new JsonObject
            {
                ["repo"] = did,
                ["collection"] = "app.bsky.feed.post",
                ["record"] = Record(message, thumb),
            };
            var createAnswer = await PostAsync(service, "com.atproto.repo.createRecord", JsonContent(body), jwt, ct);
            if (!createAnswer.IsSuccess)
                return Refused("post", createAnswer, password, jwt);

            // A success without a usable answer may still have posted: the retry's look for an earlier send settles it.
            return ReadCreated(createAnswer.Body, did, handle) ?? (AnnounceOutcome)new AnnounceOutcome.Retry("Bluesky sent an unexpected answer", null);
        }
        catch (Exception ex) when (AnnounceErrors.IsExpected(ex, ct))
        {
            return new AnnounceOutcome.Retry(SocialFetchErrors.Reason(Network, ex), null);
        }
    }

    public async Task<AnnounceOutcome> FindEarlierAsync(AnnouncementMessage message, CancellationToken ct)
    {
        var url = $"{PublicAppView}/xrpc/app.bsky.feed.getAuthorFeed?actor={Uri.EscapeDataString(options.Value.Bluesky.Did.Trim())}&limit=30&filter=posts_no_replies";
        try
        {
            var answer = await SocialHttp.GetAsync(clients.CreateClient(SocialHttp.Bluesky), url, SocialHttp.ThreadLimit, ct);
            if (!answer.IsSuccess)
                return AnnounceErrors.Classify(Network, "author feed request", answer.Status, answer.Body, () => SocialRateLimit.RetryAt(answer.Headers, Now()));

            return FindPost(answer.Body, message.PostUrl) ?? (AnnounceOutcome)new AnnounceOutcome.NotFound();
        }
        catch (Exception ex) when (AnnounceErrors.IsExpected(ex, ct))
        {
            return new AnnounceOutcome.Retry(SocialFetchErrors.Reason(Network, ex), null);
        }
    }

    private JsonObject Record(AnnouncementMessage message, JsonNode? thumb)
    {
        var record = new JsonObject
        {
            ["$type"] = "app.bsky.feed.post",
            ["text"] = message.Text,
            ["createdAt"] = Now().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(options.Value.AnnouncementLanguage))
            record["langs"] = new JsonArray(options.Value.AnnouncementLanguage.Trim());

        var facets = new JsonArray();
        foreach (var facet in BlueskyFacets.All(message.Text, message.PostUrl))
        {
            var feature = facet.Tag is not null
                ? new JsonObject { ["$type"] = "app.bsky.richtext.facet#tag", ["tag"] = facet.Tag }
                : new JsonObject { ["$type"] = "app.bsky.richtext.facet#link", ["uri"] = facet.Uri };
            facets.Add(new JsonObject
            {
                ["index"] = new JsonObject { ["byteStart"] = facet.ByteStart, ["byteEnd"] = facet.ByteEnd },
                ["features"] = new JsonArray(feature),
            });
        }

        if (facets.Count > 0)
            record["facets"] = facets;

        var external = new JsonObject { ["uri"] = message.PostUrl, ["title"] = message.Title, ["description"] = message.Subtitle ?? string.Empty };
        if (thumb is not null)
            external["thumb"] = thumb;

        record["embed"] = new JsonObject { ["$type"] = "app.bsky.embed.external", ["external"] = external };
        return record;
    }

    private async Task<SocialResponse> PostAsync(string service, string method, HttpContent content, string? jwt, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{service}/xrpc/{method}") { Content = content };
        if (jwt is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        return await SocialHttp.SendAsync(clients.CreateClient(SocialHttp.Announce), request, SocialHttp.ThreadLimit, SocialHttp.AnnounceTimeout, keepErrorBody: true, ct);
    }

    private AnnounceOutcome Refused(string what, SocialResponse answer, params string[] secrets) =>
        AnnounceErrors.Classify(Network, what, answer.Status, answer.Body, () => SocialRateLimit.RetryAt(answer.Headers, Now()), secrets);

    private static StringContent JsonContent(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private DateTime Now() => time.GetUtcNow().UtcDateTime;

    private static (string? Jwt, string? Did, string? Handle) ReadSession(byte[] body)
    {
        try
        {
            using var document = SocialJson.Parse(Encoding.UTF8.GetString(body), "session");
            var jwt = document.RootElement.Str("accessJwt");
            var did = document.RootElement.Str("did");
            return (string.IsNullOrEmpty(jwt) ? null : jwt, string.IsNullOrEmpty(did) ? null : did, document.RootElement.Str("handle"));
        }
        catch (SocialParseException)
        {
            return (null, null, null);
        }
    }

    private static JsonNode? ReadBlob(byte[] body)
    {
        try
        {
            return JsonNode.Parse(body) is JsonObject { } answer && answer["blob"] is JsonObject blob ? blob.DeepClone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AnnounceOutcome.Posted? ReadCreated(byte[] body, string did, string? handle)
    {
        try
        {
            using var document = SocialJson.Parse(Encoding.UTF8.GetString(body), "created record");
            return document.RootElement.Str("uri") is { } uri && Link(uri, did, handle) is { } url ? new AnnounceOutcome.Posted(uri, url) : null;
        }
        catch (SocialParseException)
        {
            return null;
        }
    }

    /// <summary>The bsky.app address of an <c>at://did/app.bsky.feed.post/rkey</c> URI (the author is the account whose repository holds it). The profile
    /// part is the account's <paramref name="handle"/> when Bluesky gave a valid one (readable, and what a person would paste), else the DID.</summary>
    private static string? Link(string uri, string did, string? handle)
    {
        var parts = uri.Split('/');
        if (parts is not ["at:", "", var author, "app.bsky.feed.post", { Length: > 0 } rkey])
            return null;

        var repository = author.Length > 0 ? author : did;
        var profile = SocialLinkParser.IsUsableHandle(handle) && repository.StartsWith("did:", StringComparison.Ordinal) ? handle! : repository;
        return $"https://bsky.app/profile/{profile}/post/{Uri.EscapeDataString(rkey)}";
    }

    /// <summary>The author-feed post whose card or link facet points at exactly the post's address.</summary>
    private static AnnounceOutcome.Posted? FindPost(byte[] body, string postUrl)
    {
        JsonDocument document;
        try
        {
            document = SocialJson.Parse(Encoding.UTF8.GetString(body), "author feed");
        }
        catch (SocialParseException)
        {
            return null;
        }

        using (document)
        {
            foreach (var item in document.RootElement.Prop("feed").Items())
            {
                var post = item.Prop("post");
                var record = post.Prop("record");
                var carded = string.Equals(record.Prop("embed").Prop("external").Str("uri"), postUrl, StringComparison.Ordinal);
                var linked = record.Prop("facets").Items().Any(facet => facet.Prop("features").Items().Any(feature => string.Equals(feature.Str("uri"), postUrl, StringComparison.Ordinal)));
                if ((carded || linked) && post.Str("uri") is { } uri && Link(uri, string.Empty, post.Prop("author").Str("handle")) is { } url)
                    return new AnnounceOutcome.Posted(uri, url);
            }
        }

        return null;
    }
}
