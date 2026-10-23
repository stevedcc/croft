using System.Data;
using System.Text;
using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Shared.Enums;
using Microsoft.EntityFrameworkCore;

namespace Croft.Api.Services.ContentExport;

/// <summary>
/// Writes the site's content as plain files, independent of the database (see
/// <see cref="ContentFormat"/>). One REPEATABLE READ, read-only transaction reads all the
/// content tables, so the files agree with each other; nothing is written to the database, no
/// migration or advisory lock is taken, so it also runs against a read replica. Output is
/// deterministic: unchanged content gives byte-identical files (only the manifest's
/// <c>exportedAt</c> differs).
/// </summary>
public static class ContentExporter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task<ManifestFile> ExportAsync(
        BlogDbContext db, string outputDirectory, DateTimeOffset exportedAt, string croftVersion,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var target = Path.GetFullPath(outputDirectory);
        SweepStale(target);
        var content = await ReadAsync(db, ct);

        var temporary = $"{target}.tmp-{Guid.NewGuid():N}";
        Directory.CreateDirectory(temporary);
        try
        {
            var manifest = Write(content, temporary, exportedAt, croftVersion);
            Swap(temporary, target);
            return manifest;
        }
        catch
        {
            if (Directory.Exists(temporary))
                Directory.Delete(temporary, recursive: true);
            throw;
        }
    }

    private sealed record Content(
        List<Post> Posts, List<Category> Categories, List<Tag> Tags, List<MediaAsset> Media, List<Feedback> Feedback);

    private static async Task<Content> ReadAsync(BlogDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        // Read-only on purpose: a write here is a bug, and a replica refuses it anyway.
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);

        // SearchVector (derived from title, excerpt and Markdown, rebuilt by the database) is loaded
        // with the row but never exported, and PageViews (analytics, own retention) are not read at
        // all: see ContentFormat.Exclusions.
        var posts = await db.Posts.AsNoTracking().AsSplitQuery()
            .Include(post => post.Tags)
            .Include(post => post.MediaAssets)
            .Include(post => post.Category)
            .Include(post => post.Views)
            .ToListAsync(ct);
        var categories = await db.Categories.AsNoTracking().ToListAsync(ct);
        var tags = await db.Tags.AsNoTracking().ToListAsync(ct);
        var media = await db.MediaAssets.AsNoTracking().ToListAsync(ct);
        var feedback = await db.Feedback.AsNoTracking().ToListAsync(ct);

        await transaction.CommitAsync(ct);
        return new Content(posts, categories, tags, media, feedback);
    }

    private static ManifestFile Write(Content content, string directory, DateTimeOffset exportedAt, string croftVersion)
    {
        var feedbackByPost = content.Feedback.ToLookup(item => item.PostId);
        var posts = content.Posts.Where(post => post.Kind == PostKind.Post).OrderBy(post => post.Slug, StringComparer.Ordinal).ToList();
        var pages = content.Posts.Where(post => post.Kind == PostKind.Page).OrderBy(post => post.Slug, StringComparer.Ordinal).ToList();

        WritePosts(posts, Path.Combine(directory, ContentFormat.PostsDirectory), feedbackByPost);
        WritePosts(pages, Path.Combine(directory, ContentFormat.PagesDirectory), feedbackByPost);

        WriteText(Path.Combine(directory, ContentFormat.CategoriesFile), ContentFormat.Serialize(
            content.Categories.OrderBy(item => item.Slug, StringComparer.Ordinal)
                .Select(item => new CategoryEntry(item.Id, item.Name, item.Slug, item.Icon)).ToList()));
        WriteText(Path.Combine(directory, ContentFormat.TagsFile), ContentFormat.Serialize(
            content.Tags.OrderBy(item => item.Slug, StringComparer.Ordinal)
                .Select(item => new TagEntry(item.Id, item.Name, item.Slug, item.CountsTowardsRelated)).ToList()));
        WriteText(Path.Combine(directory, ContentFormat.MediaFile), ContentFormat.Serialize(
            content.Media.OrderBy(item => Key(item.Id), StringComparer.Ordinal)
                .Select(item => new MediaEntry(item.Id, item.FileName, item.OriginalName, item.ContentType, item.AltText,
                    item.FilePath, item.UploadedAt, item.UploadedBy, item.Scope, item.PostId)).ToList()));

        var manifest = new ManifestFile(
            ContentFormat.Version,
            exportedAt.UtcDateTime,
            croftVersion,
            new ManifestCounts(posts.Count, pages.Count, content.Categories.Count, content.Tags.Count, content.Media.Count));
        WriteText(Path.Combine(directory, ContentFormat.ManifestFile), ContentFormat.Serialize(manifest));
        return manifest;
    }

    private static void WritePosts(List<Post> posts, string directory, ILookup<Guid, Feedback> feedbackByPost)
    {
        Directory.CreateDirectory(directory);
        foreach (var post in posts)
        {
            RequireSafeFileName(post.Slug);
            var markdownFile = post.Slug + ".md";

            // Exactly the stored text, UTF-8 without a BOM: no newline normalising, no trailing newline added.
            File.WriteAllBytes(Path.Combine(directory, markdownFile), Utf8NoBom.GetBytes(post.MarkdownContent));

            var file = new PostFile(
                post.Id, post.Slug, post.Kind, post.Status, post.Title, post.Subtitle, post.Excerpt, post.Summary,
                post.ReadingMinutes, post.NavigationPlacement, post.NavigationOrder, post.PublishedAt,
                post.CreatedAt, post.UpdatedAt, post.ContentEditedAt, post.AuthorId, post.AuthorName,
                post.BannerImageId, post.CategoryId, post.Category?.Slug,
                // tagIds and tagSlugs are sorted by the same key (the slug, unique), so entry i of one pairs with entry i of the other.
                [.. post.Tags.OrderBy(tag => tag.Slug, StringComparer.Ordinal).Select(tag => tag.Id)],
                [.. post.Tags.OrderBy(tag => tag.Slug, StringComparer.Ordinal).Select(tag => tag.Slug)],
                [.. post.MediaAssets.OrderBy(asset => Key(asset.Id), StringComparer.Ordinal).Select(asset => asset.Id)],
                post.Views?.ViewCount ?? 0,
                [.. feedbackByPost[post.Id].OrderBy(item => Key(item.Id), StringComparer.Ordinal)
                    .Select(item => new FeedbackEntry(item.Id, item.PostId, item.AuthorId, item.AuthorName,
                        item.QuotedText, item.Comment, item.CreatedAt, item.ResolvedAt))],
                markdownFile);
            WriteText(Path.Combine(directory, post.Slug + ".json"), ContentFormat.Serialize(file));
        }
    }

    private static string Key(Guid id) => id.ToString("D");

    private static void WriteText(string path, string text) => File.WriteAllBytes(path, Utf8NoBom.GetBytes(text));

    /// <summary>The slug names the files; one that could escape the folder or is not a plain name stops the export.</summary>
    internal static void RequireSafeFileName(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug is "." or ".."
            || slug.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || slug.Contains('/') || slug.Contains('\\'))
        {
            throw new InvalidOperationException($"The slug '{slug}' cannot be used as a file name; export stopped.");
        }
    }

    /// <summary>
    /// Replaces <paramref name="target"/> with the finished temporary directory. The previous
    /// export is moved aside to <c>target.old-*</c> first and removed only once the new one is in
    /// place; if the second move fails the previous one is moved back. A crash between the two
    /// renames leaves the good export in <c>target.old-*</c> and no target folder: the next
    /// export's <see cref="SweepStale"/> puts it back. (The target must be a normal folder, not a
    /// mount point: put the volume one level above, see k8s/README.md.)
    /// </summary>
    private static void Swap(string temporary, string target)
    {
        string? previous = null;
        if (Directory.Exists(target))
        {
            previous = $"{target}.old-{Guid.NewGuid():N}";
            Directory.Move(target, previous);
        }

        try
        {
            Directory.Move(temporary, target);
        }
        catch
        {
            if (previous is not null)
                Directory.Move(previous, target);
            throw;
        }

        if (previous is not null)
            Directory.Delete(previous, recursive: true);
    }

    /// <summary>
    /// Clears what a crashed run left next to the target: a missing target is restored from the
    /// newest <c>target.old-*</c> (the good export of the run that crashed mid-swap), then every
    /// remaining <c>target.tmp-*</c> and <c>target.old-*</c> is deleted. Assumes one export runs at
    /// a time (a CronJob with concurrencyPolicy Forbid): a concurrent run's temporary folder would
    /// be swept too.
    /// </summary>
    private static void SweepStale(string target)
    {
        var parent = Path.GetDirectoryName(target);
        if (parent is null || !Directory.Exists(parent))
            return;

        var name = Path.GetFileName(target);
        var old = Directory.GetDirectories(parent, name + ".old-*").OrderByDescending(Directory.GetLastWriteTimeUtc).ToList();
        if (!Directory.Exists(target) && old.Count > 0)
        {
            Directory.Move(old[0], target);
            old.RemoveAt(0);
        }

        foreach (var stale in old.Concat(Directory.GetDirectories(parent, name + ".tmp-*")))
            Directory.Delete(stale, recursive: true);
    }
}
