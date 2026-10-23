using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Shared.Enums;

namespace Croft.Api.Tests.Fixtures;

/// <summary>
/// Content with every field set to a non-default value: a published post (banner, category,
/// two tags, media, two feedback rows), a draft, and a footer page; Markdown with
/// front-matter-looking text, CRLF lines, emoji and non-Latin scripts.
/// </summary>
public static class ContentSeed
{
    public static readonly Guid PublishedId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    public static readonly Guid DraftId = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    public static readonly Guid PageId = Guid.Parse("00000000-0000-0000-0000-0000000000a3");

    public const string PublishedMarkdown =
        "---\r\ntitle: not really front matter\r\ntags: [a, b]\r\n---\r\n\r\nLine two with CRLF\r\n\r\n"
        + "Emoji \U0001F389 and a ZWJ sequence \U0001F469‍\U0001F4BB, Japanese 日本語, Greek Ελληνικά, Arabic مرحبا.\r\n";

    public const string DraftMarkdown = "No trailing newline, then blank lines\n\n\n";

    public const string PageMarkdown = "# About\n\nA page with a\ttab.\n";

    public static DateTime At(int day, int microseconds = 0) =>
        new DateTime(2026, 3, day, 5, 6, 7, DateTimeKind.Utc).AddTicks(microseconds * 10L);

    public static async Task SeedAsync(BlogDbContext db)
    {
        var category = new Category { Id = Guid.Parse("00000000-0000-0000-0000-0000000000c1"), Name = "Engineering", Slug = "engineering", Icon = "build" };
        var tagOne = new Tag { Id = Guid.Parse("00000000-0000-0000-0000-0000000000d1"), Name = "Dotnet", Slug = "dotnet", CountsTowardsRelated = true };
        var tagTwo = new Tag { Id = Guid.Parse("00000000-0000-0000-0000-0000000000d2"), Name = "AI assisted", Slug = "ai-assisted", CountsTowardsRelated = false };
        db.AddRange(category, tagOne, tagTwo);

        var banner = Media("e1", "banner.png", "image/png", "Banner alt text", "banners/e1.png", MediaAssetScope.Global, 10);
        var inline = Media("e2", "diagram.svg", "image/svg+xml", "Diagram of the thing", "posts/e2.svg", MediaAssetScope.PostSpecific, 11);
        var spare = Media("e3", "spare.jpg", "image/jpeg", null, "spare/e3.jpg", MediaAssetScope.Global, 12);
        db.AddRange(banner, inline, spare);
        await db.SaveChangesAsync();

        var published = new Post
        {
            Id = PublishedId, Slug = "hello-croft", Kind = PostKind.Post, Status = PostStatus.Published,
            Title = "Hello, Croft", Subtitle = "A first look", MarkdownContent = PublishedMarkdown,
            Excerpt = "The excerpt of the post", Summary = "A summary of the post", ReadingMinutes = 7,
            NavigationPlacement = NavigationPlacement.Sidebar, NavigationOrder = 4,
            PublishedAt = At(5, 123456), CreatedAt = At(1, 111111), UpdatedAt = At(7, 222222), ContentEditedAt = At(6, 333333),
            AuthorId = "admin-id", AuthorName = "Ada Admin", BannerImageId = banner.Id, CategoryId = category.Id,
            Views = new PostViewCount { ViewCount = 1234 }, Tags = [tagOne, tagTwo]
        };
        var draft = new Post
        {
            Id = DraftId, Slug = "work-in-progress", Kind = PostKind.Post, Status = PostStatus.Draft,
            Title = "Work in progress", Subtitle = "Not yet", MarkdownContent = DraftMarkdown,
            Excerpt = "Draft excerpt", Summary = "Draft summary", ReadingMinutes = 0,
            NavigationPlacement = NavigationPlacement.Footer, NavigationOrder = 9,
            CreatedAt = At(2, 444444), UpdatedAt = At(3, 555555), ContentEditedAt = At(3, 666666),
            AuthorId = "editor-id", AuthorName = "Eddie Editor", CategoryId = category.Id, Views = new PostViewCount { ViewCount = 5 }, Tags = [tagOne]
        };
        var page = new Post
        {
            Id = PageId, Slug = "about", Kind = PostKind.Page, Status = PostStatus.Published,
            Title = "About", Subtitle = null, MarkdownContent = PageMarkdown,
            Excerpt = null, Summary = null, ReadingMinutes = 2,
            NavigationPlacement = NavigationPlacement.Footer, NavigationOrder = 3,
            PublishedAt = At(8, 777777), CreatedAt = At(8, 1), UpdatedAt = At(9, 2), ContentEditedAt = At(9, 3),
            AuthorId = "admin-id", AuthorName = "Ada Admin", Views = new PostViewCount { ViewCount = 99 }
        };
        published.Feedback =
        [
            new Feedback
            {
                Id = Guid.Parse("00000000-0000-0000-0000-0000000000f1"), AuthorId = "editor-id", AuthorName = "Eddie Editor",
                QuotedText = "first look", Comment = "Tighten this \U0001F914", CreatedAt = At(6, 4), ResolvedAt = At(7, 5)
            },
            new Feedback
            {
                Id = Guid.Parse("00000000-0000-0000-0000-0000000000f2"), AuthorId = "admin-id", AuthorName = "Ada Admin",
                QuotedText = "Croft", Comment = "Open comment", CreatedAt = At(6, 6), ResolvedAt = null
            }
        ];
        db.AddRange(published, draft, page);
        await db.SaveChangesAsync();

        // A post-specific asset belongs to its post.
        inline.PostId = published.Id;
        await db.SaveChangesAsync();
    }

    private static MediaAsset Media(string id, string name, string type, string? alt, string path, MediaAssetScope scope, int day) => new()
    {
        Id = Guid.Parse($"00000000-0000-0000-0000-0000000000{id}"), FileName = name, OriginalName = "orig-" + name,
        ContentType = type, AltText = alt, FilePath = path, UploadedAt = At(day, 888888), UploadedBy = "admin-id", Scope = scope
    };
}
