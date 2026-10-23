using System.Text;
using System.Text.Json;
using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Shared.Enums;
using Microsoft.EntityFrameworkCore;

namespace Croft.Api.Services.ContentExport;

/// <summary>
/// Restores a content export (<see cref="ContentExporter"/>) into a database that has no
/// posts, pages, categories, tags or media. Everything is read and checked first, then written in
/// one transaction, so a bad export changes nothing. Ids, slugs, timestamps, statuses, view
/// counts, feedback and relations are kept exactly; the search vector is a computed column the
/// database fills in. It never merges and never overwrites: existing content refuses the import.
/// The Markdown of a post comes only from the .md file its JSON names.
/// </summary>
public static partial class ContentImporter
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static async Task<ManifestFile> ImportAsync(
        BlogDbContext db, string sourceDirectory, string mediaRoot, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);

        var directory = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(directory))
            throw new ContentTransferException($"The export folder '{directory}' does not exist.");

        var mediaRootPath = ResolveMediaRoot(mediaRoot);
        var export = Read(directory);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (await db.Posts.AnyAsync(ct) || await db.Categories.AnyAsync(ct) || await db.Tags.AnyAsync(ct) || await db.MediaAssets.AnyAsync(ct))
        {
            throw new ContentTransferException(
                "The database already has content (posts, pages, categories, tags or media). The import only fills an empty database and never merges or overwrites; nothing was changed.");
        }

        var categories = export.Categories.Select(item => new Category { Id = item.Id, Name = item.Name, Slug = item.Slug, Icon = item.Icon }).ToList();
        var tags = export.Tags.Select(item => new Tag { Id = item.Id, Name = item.Name, Slug = item.Slug, CountsTowardsRelated = item.CountsTowardsRelated }).ToList();
        // Media and posts point at each other (a post's banner, an asset's post), so the assets go in
        // first without their post and are linked once the posts exist.
        var media = export.Media.Select(item => new MediaAsset
        {
            Id = item.Id, FileName = item.FileName, OriginalName = item.OriginalName, ContentType = item.ContentType,
            AltText = item.AltText, FilePath = item.FilePath, UploadedAt = item.UploadedAt, UploadedBy = item.UploadedBy, Scope = item.Scope
        }).ToList();
        db.AddRange(categories);
        db.AddRange(tags);
        db.AddRange(media);
        await db.SaveChangesAsync(ct);

        var tagsById = tags.ToDictionary(tag => tag.Id);
        var posts = export.Posts.Select(entry => ToPost(entry, tagsById)).ToList();
        db.AddRange(posts);
        await db.SaveChangesAsync(ct);

        var mediaById = media.ToDictionary(asset => asset.Id);
        foreach (var item in export.Media.Where(item => item.PostId is not null))
            mediaById[item.Id].PostId = item.PostId;
        await db.SaveChangesAsync(ct);

        // Running API instances may hold the empty site in their cache: the NOTIFY joins the import's
        // transaction, so they are told when (and only if) the content commits.
        var payload = CacheNotifier.Payload(CacheGroup.All);
        await db.Database.ExecuteSqlAsync($"SELECT pg_notify({CacheNotifier.Channel}, {payload})", ct);

        await transaction.CommitAsync(ct);

        // After the commit nothing may change the outcome: the check below never throws.
        foreach (var asset in export.Media.Where(item => !MediaFileExists(mediaRootPath, item.FilePath)))
            LogMediaFileMissing(logger, asset.FilePath, asset.Id, mediaRootPath);

        return export.Manifest;
    }

    private static Post ToPost(Loaded entry, Dictionary<Guid, Tag> tagsById)
    {
        var file = entry.File;
        return new Post
        {
            Id = file.Id, Slug = file.Slug, Kind = file.Kind, Status = file.Status, Title = file.Title,
            Subtitle = file.Subtitle,
            MarkdownContent = entry.Markdown, Excerpt = file.Excerpt, Summary = file.Summary,
            ReadingMinutes = file.ReadingMinutes, NavigationPlacement = file.NavigationPlacement,
            NavigationOrder = file.NavigationOrder, PublishedAt = file.PublishedAt, CreatedAt = file.CreatedAt,
            UpdatedAt = file.UpdatedAt, ContentEditedAt = file.ContentEditedAt, AuthorId = file.AuthorId,
            AuthorName = file.AuthorName, BannerImageId = file.BannerImageId, CategoryId = file.CategoryId,
            Views = file.ViewCount > 0 ? new PostViewCount { ViewCount = file.ViewCount } : null,
            Tags = [.. file.TagIds.Select(id => tagsById[id])],
            Feedback = [.. file.Feedback.Select(item => new Feedback
            {
                Id = item.Id, AuthorId = item.AuthorId, AuthorName = item.AuthorName, QuotedText = item.QuotedText,
                Comment = item.Comment, CreatedAt = item.CreatedAt, ResolvedAt = item.ResolvedAt
            })]
        };
    }

    /// <summary>The media root, resolved before the first write so that an empty or unusable setting fails the import up front.</summary>
    private static string ResolveMediaRoot(string mediaRoot)
    {
        if (string.IsNullOrWhiteSpace(mediaRoot))
            throw new ContentTransferException("FileStorage:BasePath is empty; the import checks the media files under it, so it must name the media folder.");

        try
        {
            return Path.GetFullPath(mediaRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ContentTransferException($"FileStorage:BasePath '{mediaRoot}' is not a usable folder path: {exception.Message}");
        }
    }

    /// <summary>True only when the file is there; a path that cannot be checked at all (any path or I/O problem) counts as missing.</summary>
    private static bool MediaFileExists(string root, string filePath)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(root, filePath));
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(full);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException
            or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record Loaded(PostFile File, string Markdown);

    private sealed record Export(
        ManifestFile Manifest, List<CategoryEntry> Categories, List<TagEntry> Tags, List<MediaEntry> Media, List<Loaded> Posts);

    /// <summary>Reads and checks the whole export; throws <see cref="ContentTransferException"/> before anything is written.</summary>
    private static Export Read(string directory)
    {
        var manifest = ReadJson<ManifestFile>(Path.Combine(directory, ContentFormat.ManifestFile));
        if (manifest.FormatVersion != ContentFormat.Version)
        {
            throw new ContentTransferException(
                $"This export has formatVersion {manifest.FormatVersion}; this version of Croft reads formatVersion {ContentFormat.Version}.");
        }

        var categories = ReadJson<List<CategoryEntry>>(Path.Combine(directory, ContentFormat.CategoriesFile));
        var tags = ReadJson<List<TagEntry>>(Path.Combine(directory, ContentFormat.TagsFile));
        var media = ReadJson<List<MediaEntry>>(Path.Combine(directory, ContentFormat.MediaFile));
        var posts = ReadPosts(Path.Combine(directory, ContentFormat.PostsDirectory), PostKind.Post);
        var pages = ReadPosts(Path.Combine(directory, ContentFormat.PagesDirectory), PostKind.Page);

        RequireCount("posts", manifest.Counts.Posts, posts.Count);
        RequireCount("pages", manifest.Counts.Pages, pages.Count);
        RequireCount("categories", manifest.Counts.Categories, categories.Count);
        RequireCount("tags", manifest.Counts.Tags, tags.Count);
        RequireCount("media", manifest.Counts.Media, media.Count);

        var all = posts.Concat(pages).ToList();
        var categoryIds = categories.Select(item => item.Id).ToHashSet();
        var tagIds = tags.Select(item => item.Id).ToHashSet();
        var mediaIds = media.Select(item => item.Id).ToHashSet();
        var postIds = all.Select(item => item.File.Id).ToHashSet();
        foreach (var (file, _) in all)
        {
            Require(file.CategoryId is null || categoryIds.Contains(file.CategoryId.Value), file.Slug, "its category is not in categories.json");
            Require(file.BannerImageId is null || mediaIds.Contains(file.BannerImageId.Value), file.Slug, "its banner image is not in media.json");
            Require(file.TagIds.All(tagIds.Contains), file.Slug, "a tag it names is not in tags.json");
            Require(file.MediaAssetIds.All(mediaIds.Contains), file.Slug, "a media asset it names is not in media.json");
            Require(file.Feedback.All(item => item.PostId == file.Id), file.Slug, "a feedback entry names another post");
            // mediaAssetIds (here) and each asset's postId (media.json) state the same relation; they must agree.
            Require(file.MediaAssetIds.ToHashSet().SetEquals(media.Where(item => item.PostId == file.Id).Select(item => item.Id)), file.Slug,
                "its mediaAssetIds do not match the assets media.json assigns to it (postId)");
        }

        Require(media.All(item => item.PostId is null || postIds.Contains(item.PostId.Value)), ContentFormat.MediaFile, "an asset names a post that is not in the export");
        return new Export(manifest, categories, tags, media, all);
    }

    private static List<Loaded> ReadPosts(string directory, PostKind kind)
    {
        if (!Directory.Exists(directory))
            return [];

        var result = new List<Loaded>();
        foreach (var path in Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var file = ReadJson<PostFile>(path);
            var name = Path.GetFileNameWithoutExtension(path);
            Require(file.Slug == name, path, $"its slug '{file.Slug}' does not match the file name");
            Require(file.Kind == kind, path, $"it is a {file.Kind.ToString().ToLowerInvariant()}, not in the {ContentFormat.PostsDirectory}/{ContentFormat.PagesDirectory} folder it sits in");
            // The Markdown is the .md the JSON names, beside it; the name is checked, never trusted as a path.
            Require(file.MarkdownFile == file.Slug + ".md", path,
                $"markdownFile '{file.MarkdownFile}' is not '{file.Slug}.md', the file that belongs to this {file.Kind.ToString().ToLowerInvariant()}");

            var markdownPath = Path.Combine(directory, file.MarkdownFile);
            if (!File.Exists(markdownPath))
                throw new ContentTransferException($"The Markdown file '{markdownPath}' named by '{path}' is missing.");

            string markdown;
            try
            {
                // GetString, not ReadAllText: a leading BOM character would stay part of the text instead of being dropped.
                markdown = StrictUtf8.GetString(File.ReadAllBytes(markdownPath));
            }
            catch (DecoderFallbackException)
            {
                throw new ContentTransferException($"The Markdown file '{markdownPath}' is not valid UTF-8.");
            }

            result.Add(new Loaded(file, markdown));
        }

        return result;
    }

    private static T ReadJson<T>(string path)
    {
        if (!File.Exists(path))
            throw new ContentTransferException($"The export file '{path}' is missing.");

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), ContentFormat.Json)
                ?? throw new ContentTransferException($"The export file '{path}' is empty.");
        }
        catch (JsonException exception)
        {
            throw new ContentTransferException($"The export file '{path}' cannot be read: {exception.Message}");
        }
    }

    private static void RequireCount(string kind, int expected, int found) =>
        Require(expected == found, ContentFormat.ManifestFile, $"it says {expected} {kind} but the export has {found}");

    private static void Require(bool condition, string where, string problem)
    {
        if (!condition)
            throw new ContentTransferException($"The export is not consistent: {where}: {problem}.");
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Media file '{FilePath}' of asset {AssetId} is not under the media root '{MediaRoot}'; the row was restored, restore the file from the file backup.")]
    private static partial void LogMediaFileMissing(ILogger logger, string filePath, Guid assetId, string mediaRoot);
}
