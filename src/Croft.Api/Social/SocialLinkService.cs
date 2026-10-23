using Croft.Api.Data;
using Croft.Api.Errors;
using Croft.Api.Models;
using Croft.Shared.Contracts.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Croft.Api.Social;

public sealed class SocialLinkService(BlogDbContext db, IOptions<SocialOptions> options, IBlueskyHandleResolver resolver) : ISocialLinkService
{
    public async Task<ResolvedSocialLinks> ResolveAsync(string? mastodonUrl, string? blueskyUrl, bool nullMeansUnchanged = false, CancellationToken ct = default)
    {
        var mastodonUnchanged = nullMeansUnchanged && mastodonUrl is null;
        var blueskyUnchanged = nullMeansUnchanged && blueskyUrl is null;
        var settings = options.Value;
        string? mastodon = Normalise(mastodonUrl);
        string? bluesky = Normalise(blueskyUrl);

        string? statusId = mastodon is null ? null : SocialLinkParser.ParseMastodon(mastodon, settings);

        string? uri = null;
        if (bluesky is not null)
        {
            if (!settings.BlueskyConfigured)
                throw new ValidationException($"{SocialLinkParser.BlueskyField}: Bluesky is not configured on this site (Social:Bluesky:Did), so no post can be linked.");

            var link = SocialLinkParser.ParseBluesky(bluesky);
            var did = SocialLinkParser.IsDid(link.Actor)
                ? link.Actor
                : await resolver.ResolveDidAsync(link.Actor.ToLowerInvariant(), ct);
            if (!string.Equals(did, settings.Bluesky.Did.Trim(), StringComparison.Ordinal))
                throw new ValidationException($"{SocialLinkParser.BlueskyField} must be a post of the configured Bluesky account.");

            uri = SocialLinkParser.BlueskyUri(did, link.RecordKey);
        }

        return new ResolvedSocialLinks(mastodon, statusId, bluesky, uri, mastodonUnchanged, blueskyUnchanged);
    }

    public async Task<bool> ApplyAsync(Post post, ResolvedSocialLinks links, DateTime now, CancellationToken ct = default)
    {
        var isNew = db.Entry(post).State is EntityState.Detached or EntityState.Added;
        var threads = isNew
            ? []
            : await db.SocialThreads.Where(t => t.PostId == post.Id).ToListAsync(ct);

        // A platform the request did not mention keeps its link, its thread and its replies exactly as they are.
        var mastodon = links.MastodonUnchanged ? (Changed: false, LinkSetOrChanged: false) : await ApplyPlatformAsync(post, SocialPlatform.Mastodon, links.MastodonUrl, links.MastodonStatusId,
            post.MastodonStatusId, isNew, threads, now, ct);
        var bluesky = links.BlueskyUnchanged ? (Changed: false, LinkSetOrChanged: false) : await ApplyPlatformAsync(post, SocialPlatform.Bluesky, links.BlueskyUrl, links.BlueskyUri,
            post.BlueskyUri, isNew, threads, now, ct);

        if (!links.MastodonUnchanged)
        {
            post.MastodonUrl = links.MastodonUrl;
            post.MastodonStatusId = links.MastodonStatusId;
        }

        if (!links.BlueskyUnchanged)
        {
            post.BlueskyUrl = links.BlueskyUrl;
            post.BlueskyUri = links.BlueskyUri;
        }

        if (mastodon.LinkSetOrChanged || bluesky.LinkSetOrChanged)
            post.DiscussionLinkedAt = now;

        return mastodon.Changed || bluesky.Changed;
    }

    private async Task<(bool Changed, bool LinkSetOrChanged)> ApplyPlatformAsync(
        Post post, SocialPlatform platform, string? newUrl, string? newId, string? currentId, bool isNew,
        List<SocialThread> threads, DateTime now, CancellationToken ct)
    {
        var thread = threads.FirstOrDefault(t => t.Platform == platform);

        if (newId is null)
        {
            if (currentId is null && thread is null)
                return (false, false);

            await RemoveThreadAsync(post, platform, thread, isNew, ct);
            return (true, false);
        }

        if (newId == currentId && thread is not null)
            return (false, false); // The same post: its thread, replies and clock stay as they are.

        // A new post, or another post than before: the old replies are not replies of this one.
        if (!isNew && currentId is not null && newId != currentId)
            db.SocialReplies.RemoveRange(await db.SocialReplies.Where(r => r.PostId == post.Id && r.Platform == platform).ToListAsync(ct));

        if (thread is null)
        {
            db.SocialThreads.Add(new SocialThread { Post = post, Platform = platform, NextDueAt = now });
        }
        else
        {
            thread.SnapshotHash = null;
            thread.LastSuccessAt = null;
            thread.LastAttemptAt = null;
            thread.LastError = null;
            thread.LeaseUntil = null;
            thread.NextDueAt = now;
        }

        return (true, true);
    }

    private async Task RemoveThreadAsync(Post post, SocialPlatform platform, SocialThread? thread, bool isNew, CancellationToken ct)
    {
        if (thread is not null)
            db.SocialThreads.Remove(thread);

        if (!isNew)
            db.SocialReplies.RemoveRange(await db.SocialReplies.Where(r => r.PostId == post.Id && r.Platform == platform).ToListAsync(ct));
    }

    private static string? Normalise(string? url) => string.IsNullOrWhiteSpace(url) ? null : url.Trim();
}
