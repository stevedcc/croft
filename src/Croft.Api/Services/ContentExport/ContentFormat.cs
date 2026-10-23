using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Croft.Api.Models;
using Croft.Shared.Enums;

namespace Croft.Api.Services.ContentExport;

/// <summary>
/// The content export's file format (formatVersion 1): the records below are the JSON files, in
/// the order their properties are declared. Each record property is named after the entity
/// property it exports (so the completeness guard can match them); where a property exports a
/// relation or renames a field it says so with <see cref="CoversAttribute"/>.
/// </summary>
public static class ContentFormat
{
    public const int Version = 1;

    /// <summary>The retired shadow property of Post (no CLR property): the guard finds it through the EF model.</summary>
    public const string PostShowInNavigation = "ShowInNavigation";

    public const string ManifestFile = "manifest.json";
    public const string CategoriesFile = "categories.json";
    public const string TagsFile = "tags.json";
    public const string MediaFile = "media.json";
    public const string PostsDirectory = "posts";
    public const string PagesDirectory = "pages";

    /// <summary>
    /// Entity properties that are deliberately not exported, each with the reason. The
    /// completeness guard (ContentExportCompletenessTests) fails for any public settable
    /// property that is neither exported (a record property of the same name, or one that
    /// <see cref="CoversAttribute"/> names) nor listed here.
    /// </summary>
    public static readonly IReadOnlyDictionary<Type, IReadOnlyDictionary<string, string>> Exclusions =
        new Dictionary<Type, IReadOnlyDictionary<string, string>>
        {
            [typeof(Post)] = new Dictionary<string, string>
            {
                [nameof(Post.SearchVector)] = "derived: a stored computed column the database rebuilds from title, excerpt and Markdown",
                [nameof(Post.PageViews)] = "analytics, not content: page views have their own retention",
                [nameof(Post.LegacyViewCount)] = "a pending-contract column (see AGENTS.md), kept only for releases older than the release of brief 13c (the release after 0.6.3); the view total is exported as viewCount from PostViewCounts (Post.Views)",
                [nameof(Post.MastodonUrl)] = "social discussion link (brief 12a), outside format version 1: the owner re-enters it after a restore, and the replies are re-fetched from it",
                [nameof(Post.MastodonStatusId)] = "derived from MastodonUrl on save",
                [nameof(Post.BlueskyUrl)] = "social discussion link (brief 12a), outside format version 1: the owner re-enters it after a restore, and the replies are re-fetched from it",
                [nameof(Post.BlueskyUri)] = "derived from BlueskyUrl on save (a handle resolved to a DID)",
                [nameof(Post.DiscussionLinkedAt)] = "the fetch schedule's clock for the link, which is not exported",
                [nameof(Post.AnnouncementText)] = "announcing setting (brief 12d), outside format version 1: it only matters until the post is published, and a restore starts with the defaults",
                [nameof(Post.AnnounceMastodon)] = "announcing setting (brief 12d), outside format version 1: an imported post announces nothing, so a restore cannot announce old posts by surprise",
                [nameof(Post.AnnounceBluesky)] = "announcing setting (brief 12d), outside format version 1: an imported post announces nothing, so a restore cannot announce old posts by surprise",
                [PostShowInNavigation] = "a pending-contract column (see AGENTS.md), kept only for releases older than 0.6.1 and derived from NavigationPlacement, which is exported; "
                    + "an import leaves it at its database default (false) and a later contract migration drops it"
            },
            [typeof(Category)] = new Dictionary<string, string>
            {
                [nameof(Category.Posts)] = "inverse of Post.CategoryId, which each post exports"
            },
            [typeof(Tag)] = new Dictionary<string, string>
            {
                [nameof(Tag.Posts)] = "inverse of Post.Tags, which each post exports as tagIds"
            }
        };

    /// <summary>The record that exports each entity.</summary>
    public static readonly IReadOnlyDictionary<Type, Type> Records = new Dictionary<Type, Type>
    {
        [typeof(Post)] = typeof(PostFile),
        [typeof(Category)] = typeof(CategoryEntry),
        [typeof(Tag)] = typeof(TagEntry),
        [typeof(MediaAsset)] = typeof(MediaEntry),
        [typeof(Feedback)] = typeof(FeedbackEntry)
    };

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        // Plain text for people reading or diffing the files; the files are never put in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new UtcDateTimeConverter() }
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json) + "\n";
}

/// <summary>Marks that a record property exports the named entity properties (a relation, or a renamed field).</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public sealed class CoversAttribute(string entityProperty) : Attribute
{
    public string EntityProperty { get; } = entityProperty;
}

public sealed record ManifestFile(
    int FormatVersion,
    DateTime ExportedAt,
    string CroftVersion,
    ManifestCounts Counts);

public sealed record ManifestCounts(int Posts, int Pages, int Categories, int Tags, int Media);

public sealed record PostFile(
    Guid Id,
    string Slug,
    PostKind Kind,
    PostStatus Status,
    string Title,
    string? Subtitle,
    string? Excerpt,
    string? Summary,
    int ReadingMinutes,
    NavigationPlacement NavigationPlacement,
    int NavigationOrder,
    DateTime? PublishedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime ContentEditedAt,
    string AuthorId,
    string AuthorName,
    [property: Covers(nameof(Post.BannerImage))] Guid? BannerImageId,
    [property: Covers(nameof(Post.Category))] Guid? CategoryId,
    // For readers of the file; the import uses the ids.
    [property: Covers(nameof(Post.Category))] string? CategorySlug,
    [property: Covers(nameof(Post.Tags))] IReadOnlyList<Guid> TagIds,
    [property: Covers(nameof(Post.Tags))] IReadOnlyList<string> TagSlugs,
    [property: Covers(nameof(Post.MediaAssets))] IReadOnlyList<Guid> MediaAssetIds,
    [property: Covers(nameof(Post.Views))] long ViewCount,
    IReadOnlyList<FeedbackEntry> Feedback,
    [property: Covers(nameof(Post.MarkdownContent))] string MarkdownFile);

public sealed record FeedbackEntry(
    Guid Id,
    [property: Covers(nameof(Feedback.Post))] Guid PostId,
    string AuthorId,
    string AuthorName,
    string QuotedText,
    string Comment,
    DateTime CreatedAt,
    DateTime? ResolvedAt);

public sealed record CategoryEntry(Guid Id, string Name, string Slug, string? Icon);

public sealed record TagEntry(Guid Id, string Name, string Slug, bool CountsTowardsRelated);

public sealed record MediaEntry(
    Guid Id,
    string FileName,
    string OriginalName,
    string ContentType,
    string? AltText,
    string FilePath,
    DateTime UploadedAt,
    string UploadedBy,
    MediaAssetScope Scope,
    [property: Covers(nameof(MediaAsset.Post))] Guid? PostId);

/// <summary>UTC ISO 8601 with a trailing Z and no trailing zeros in the fraction; reads only UTC-convertible text.</summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTime.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString(Format, System.Globalization.CultureInfo.InvariantCulture));
}
