namespace Croft.Shared;

/// <summary>
/// The sizes the public reading pages, the feed and the sitemap ask the API for. They live here,
/// not as literals in each caller, because the API's warm-up (brief 14a) must send the same query
/// shapes the Web does: one place to change a size, so the two cannot drift apart.
/// </summary>
public static class PublicPageSizes
{
    /// <summary>Posts per page on Home, Category, Tag.</summary>
    public const int PostList = 10;

    /// <summary>Results per page on Search.</summary>
    public const int Search = 10;

    /// <summary>Categories per page on the Categories page.</summary>
    public const int Categories = 24;

    /// <summary>Related posts under a post.</summary>
    public const int Related = 3;

    /// <summary>Entries in feed.xml.</summary>
    public const int FeedEntries = 20;

    /// <summary>Rows per request of the pages list and the tags list (the sitemap used to walk them; since brief 14b it reads /api/sitemap, and the API warm-up still sends these two shapes).</summary>
    public const int Sitemap = 100;

    /// <summary>Top categories the sidebar asks for.</summary>
    public const int SidebarCategories = 12;
}
