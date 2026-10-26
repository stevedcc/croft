using Croft.Api.Models;
using Croft.Api.Social;

namespace Croft.Api.Tests.Fixtures.Social;

/// <summary>For tests that build a <c>PostService</c> by hand and are not about discussion links: no link, nothing to apply.</summary>
internal sealed class NoSocialLinks : ISocialLinkService
{
    /// <summary>The queue of such a test: social is off, so it queues nothing.</summary>
    public static IAnnouncementOutbox Outbox(Croft.Api.Data.BlogDbContext db) => new AnnouncementOutbox(db, Options);

    /// <summary>The counts reader of such a test: social is off, so it reads nothing.</summary>
    public static ISocialCountsReader Counts(Croft.Api.Data.BlogDbContext db) => new SocialCountsReader(db, Croft.Api.Tests.Fixtures.TestCache.NewCache(), Options);

    /// <summary>The settings of such a test: social is off, nothing is announced.</summary>
    public static Microsoft.Extensions.Options.IOptions<SocialOptions> Options => Microsoft.Extensions.Options.Options.Create(new SocialOptions());

    public Task<ResolvedSocialLinks> ResolveAsync(string? mastodonUrl, string? blueskyUrl, bool nullMeansUnchanged = false, CancellationToken ct = default) =>
        Task.FromResult(new ResolvedSocialLinks(null, null, null, null));

    public Task<bool> ApplyAsync(Post post, ResolvedSocialLinks links, DateTime now, CancellationToken ct = default) => Task.FromResult(false);
}
