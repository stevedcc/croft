using Croft.Shared.Enums;
using NpgsqlTypes;

namespace Croft.Api.Models;

public class Post
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public string MarkdownContent { get; set; } = string.Empty;
    public string? Excerpt { get; set; }
    public string? Summary { get; set; }
    public int ReadingMinutes { get; set; } = 1;
    public PostStatus Status { get; set; }

    /// <summary>A page is a kind of post: pages live at the Web root (/<c>slug</c>), posts under /post.</summary>
    public PostKind Kind { get; set; } = PostKind.Post;

    /// <summary>Pages only: where the page is linked from (nowhere, sidebar or footer).</summary>
    public NavigationPlacement NavigationPlacement { get; set; } = NavigationPlacement.None;

    /// <summary>Pages only: the ordering key within the sidebar or footer, ascending, then title.</summary>
    public int NavigationOrder { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>The moment of the last content edit (create/update) — the reader-facing "Updated"
    /// date. <see cref="UpdatedAt"/> is the row's last-change time and also moves on publish,
    /// unpublish and scheduling, so it cannot serve that purpose.</summary>
    public DateTime ContentEditedAt { get; set; }
    public string AuthorId { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;

    public Guid? BannerImageId { get; set; }
    public MediaAsset? BannerImage { get; set; }

    public Guid? CategoryId { get; set; }
    public Category? Category { get; set; }

    public ICollection<Tag> Tags { get; set; } = [];
    public ICollection<MediaAsset> MediaAssets { get; set; } = [];
    public ICollection<Feedback> Feedback { get; set; } = [];
    public ICollection<PageView> PageViews { get; set; } = [];

    /// <summary>The post's view total: a row of its own, so counting a view never rewrites this row.</summary>
    public PostViewCount? Views { get; set; }
    public NpgsqlTsVector SearchVector { get; set; } = null!;

    /// <summary>The linked Mastodon post (an announcement of this post on the owner's account), validated on save; null = not linked.</summary>
    public string? MastodonUrl { get; set; }

    /// <summary>The status id inside <see cref="MastodonUrl"/>: what the context fetch asks for.</summary>
    public string? MastodonStatusId { get; set; }

    /// <summary>The linked Bluesky post as entered (a bsky.app URL); null = not linked.</summary>
    public string? BlueskyUrl { get; set; }

    /// <summary>The at:// URI resolved from <see cref="BlueskyUrl"/> at save: what the thread fetch asks for.</summary>
    public string? BlueskyUri { get; set; }

    /// <summary>When a discussion link was first set or last changed: the fetch schedule's clock for a post published before its link.</summary>
    public DateTime? DiscussionLinkedAt { get; set; }

    /// <summary>The announcement's own text (brief 12d); null = the default (title and subtitle). The post's address is added at the end when it is sent.</summary>
    public string? AnnouncementText { get; set; }

    /// <summary>Announce on Mastodon when the post is published (posts only).</summary>
    public bool AnnounceMastodon { get; set; }

    /// <summary>Announce on Bluesky when the post is published (posts only).</summary>
    public bool AnnounceBluesky { get; set; }
}
