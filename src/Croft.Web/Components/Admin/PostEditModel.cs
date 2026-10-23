using Croft.Shared.Contracts;
using Croft.Shared.Enums;

namespace Croft.Web.Components.Admin;

/// <summary>
/// The post editor's mutable form state: PostEditorForm edits it in place, the page maps it to
/// and from the post DTO on load and save, and it feeds the Preview tab directly.
/// </summary>
public sealed class PostEditModel
{
    public string Title { get; set; } = string.Empty;

    public string Subtitle { get; set; } = string.Empty;

    /// <summary>The "Search &amp; link-preview description" field (spec §6.2).</summary>
    public string Excerpt { get; set; } = string.Empty;

    public string MarkdownContent { get; set; } = string.Empty;

    public Guid? CategoryId { get; set; }

    /// <summary>The chosen category (the picker's value) whose id is <see cref="CategoryId"/>.</summary>
    public CategoryDto? Category { get; set; }

    /// <summary>Tag names; a name no existing tag carries creates a new tag on save.</summary>
    public List<string> Tags { get; set; } = [];

    public MediaAssetDto? Banner { get; set; }

    public Guid? BannerImageId { get; set; }

    /// <summary>Publish moment (UTC), date part; combined with the time via UtcDateTime.Combine.</summary>
    public DateTime? PublishedAtDate { get; set; }

    /// <summary>Publish moment (UTC), time part.</summary>
    public TimeSpan? PublishedAtTime { get; set; }

    /// <summary>Pages only: where the page is linked from (nowhere, the sidebar or the footer).</summary>
    public NavigationPlacement NavigationPlacement { get; set; }

    /// <summary>Pages only: the page's place among the pages of its placement (NavigationOrder).</summary>
    public int NavigationOrder { get; set; }

    /// <summary>Posts only: the Mastodon post that announces this one, as typed (empty = none). Not part of the draft backup.</summary>
    public string MastodonUrl { get; set; } = string.Empty;

    /// <summary>Posts only: the Bluesky post that announces this one, as typed (empty = none). Not part of the draft backup.</summary>
    public string BlueskyUrl { get; set; } = string.Empty;

    /// <summary>The API's refusal of <see cref="MastodonUrl"/> from the last save, shown under its field; cleared when the text changes.</summary>
    public string? MastodonUrlError { get; set; }

    /// <summary>The API's refusal of <see cref="BlueskyUrl"/> from the last save, shown under its field; cleared when the text changes.</summary>
    public string? BlueskyUrlError { get; set; }

    /// <summary>Posts only: the announcement's own words (empty = the default, title and subtitle). The post's address is added at the end. Not part of the draft backup.</summary>
    public string AnnouncementText { get; set; } = string.Empty;

    /// <summary>Posts only: announce on Mastodon when the post is published. On for a new post.</summary>
    public bool AnnounceMastodon { get; set; } = true;

    /// <summary>Posts only: announce on Bluesky when the post is published. On for a new post.</summary>
    public bool AnnounceBluesky { get; set; } = true;

    /// <summary>The API's refusal of <see cref="AnnouncementText"/> from the last save (too long for a network), shown under its field; cleared when the text changes.</summary>
    public string? AnnouncementTextError { get; set; }
}
