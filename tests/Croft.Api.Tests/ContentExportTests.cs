using System.Data.Common;
using System.Text;
using System.Text.Json;
using Croft.Api.Data;
using Croft.Api.Services.ContentExport;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts.Social;
using Croft.Shared.Enums;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Croft.Api.Tests;

/// <summary>
/// The content export (brief 11c item 1): plain files, one JSON per post or page with its
/// Markdown beside it, readable without a database. Asserts the files' content, not just that
/// they exist.
/// </summary>
public sealed class ContentExportTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset ExportedAt = new(2026, 10, 19, 3, 0, 0, TimeSpan.Zero);
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly List<string> _directories = [];
    private ScratchDatabase _database = null!;

    public async ValueTask InitializeAsync()
    {
        _database = await ScratchDatabase.CreateAsync(fixture.ConnectionString);
        await using var db = _database.CreateContext();
        await ContentSeed.SeedAsync(db);
    }

    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync();
        foreach (var directory in _directories.Where(Directory.Exists))
            Directory.Delete(directory, recursive: true);
    }

    private string NewParent()
    {
        var parent = Path.Combine(Path.GetTempPath(), "croft-export-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        _directories.Add(parent);
        return parent;
    }

    private async Task<ManifestFile> ExportAsync(string directory, string? connectionOptions = null)
    {
        await using var db = _database.CreateContext(connectionOptions);
        return await ContentExporter.ExportAsync(db, directory, ExportedAt, "9.9.9-test");
    }

    [Fact]
    public async Task Export_WritesTheSpecifiedFiles_WithMarkdownByteForByte_AndTheManifestCounts()
    {
        var output = Path.Combine(NewParent(), "content");

        var manifest = await ExportAsync(output);

        Directory.GetFiles(output, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(output, path).Replace('\\', '/')).Order(StringComparer.Ordinal)
            .Should().Equal(
                "categories.json", "manifest.json", "media.json",
                "pages/about.json", "pages/about.md",
                "posts/hello-croft.json", "posts/hello-croft.md",
                "posts/work-in-progress.json", "posts/work-in-progress.md",
                "tags.json");

        (await File.ReadAllBytesAsync(Path.Combine(output, "posts/hello-croft.md")))
            .Should().Equal(Utf8NoBom.GetBytes(ContentSeed.PublishedMarkdown), "the Markdown is stored exactly, CRLF and all, with no BOM");
        (await File.ReadAllBytesAsync(Path.Combine(output, "posts/work-in-progress.md")))
            .Should().Equal(Utf8NoBom.GetBytes(ContentSeed.DraftMarkdown));
        (await File.ReadAllBytesAsync(Path.Combine(output, "pages/about.md")))
            .Should().Equal(Utf8NoBom.GetBytes(ContentSeed.PageMarkdown));

        var written = Deserialize<ManifestFile>(output, "manifest.json");
        written.FormatVersion.Should().Be(1);
        written.ExportedAt.Should().Be(ExportedAt.UtcDateTime);
        written.CroftVersion.Should().Be("9.9.9-test");
        written.Counts.Should().Be(new ManifestCounts(Posts: 2, Pages: 1, Categories: 1, Tags: 2, Media: 3));
        written.Should().BeEquivalentTo(manifest);

        foreach (var json in new[] { "posts/hello-croft.json", "posts/work-in-progress.json", "pages/about.json" })
        {
            var file = Deserialize<PostFile>(output, json);
            file.MarkdownFile.Should().Be(Path.GetFileNameWithoutExtension(json) + ".md");
            File.Exists(Path.Combine(output, Path.GetDirectoryName(json)!, file.MarkdownFile))
                .Should().BeTrue("markdownFile names the .md beside the JSON");
        }
    }

    [Fact]
    public async Task Export_WritesEveryFieldOfThePublishedPost_WithRelationsByStableKey()
    {
        var output = Path.Combine(NewParent(), "content");
        await ExportAsync(output);

        var post = Deserialize<PostFile>(output, "posts/hello-croft.json");

        post.Should().BeEquivalentTo(new PostFile(
            ContentSeed.PublishedId, "hello-croft", PostKind.Post, PostStatus.Published, "Hello, Croft", "A first look",
            "The excerpt of the post", "A summary of the post", 7, NavigationPlacement.Sidebar, 4,
            ContentSeed.At(5, 123456), ContentSeed.At(1, 111111), ContentSeed.At(7, 222222), ContentSeed.At(6, 333333),
            "admin-id", "Ada Admin",
            BannerImageId: Guid.Parse("00000000-0000-0000-0000-0000000000e1"),
            CategoryId: Guid.Parse("00000000-0000-0000-0000-0000000000c1"), CategorySlug: "engineering",
            TagIds: [Guid.Parse("00000000-0000-0000-0000-0000000000d2"), Guid.Parse("00000000-0000-0000-0000-0000000000d1")],
            TagSlugs: ["ai-assisted", "dotnet"],
            MediaAssetIds: [Guid.Parse("00000000-0000-0000-0000-0000000000e2")],
            ViewCount: 1234,
            Feedback:
            [
                new FeedbackEntry(Guid.Parse("00000000-0000-0000-0000-0000000000f1"), ContentSeed.PublishedId, "editor-id",
                    "Eddie Editor", "first look", "Tighten this \U0001F914", ContentSeed.At(6, 4), ContentSeed.At(7, 5)),
                new FeedbackEntry(Guid.Parse("00000000-0000-0000-0000-0000000000f2"), ContentSeed.PublishedId, "admin-id",
                    "Ada Admin", "Croft", "Open comment", ContentSeed.At(6, 6), null)
            ],
            MarkdownFile: "hello-croft.md"), options => options.WithStrictOrdering());

        var page = Deserialize<PostFile>(output, "pages/about.json");
        (page.Subtitle, page.Excerpt, page.Summary).Should().Be((null, null, null), "absent optional text stays null, not an empty string");

        var slugById = Deserialize<List<TagEntry>>(output, "tags.json").ToDictionary(tag => tag.Id, tag => tag.Slug);
        post.TagIds.Select(id => slugById[id]).Should().Equal(post.TagSlugs, "entry i of tagIds and of tagSlugs name the same tag");

        var draft = Deserialize<PostFile>(output, "posts/work-in-progress.json");
        draft.Status.Should().Be(PostStatus.Draft, "drafts are exported too");
        draft.PublishedAt.Should().BeNull();
        draft.ReadingMinutes.Should().Be(0, "a stored 0 is exported as 0, not turned into the column default 1");

        var raw = await File.ReadAllTextAsync(Path.Combine(output, "posts/hello-croft.json"));
        raw.Should().EndWith("}\n", "one trailing newline");
        raw.Should().Contain("\"status\": \"published\"").And.Contain("\"kind\": \"post\"", "enums are strings")
            .And.Contain("\"navigationPlacement\": \"sidebar\"")
            .And.Contain("\"publishedAt\": \"2026-03-05T05:06:07.123456Z\"", "UTC ISO 8601 with Z")
            .And.Contain("\"authorName\": \"Ada Admin\"", "text stays readable");
        raw.Should().NotContain("\r");
        raw.IndexOf("\"id\"", StringComparison.Ordinal).Should().BeLessThan(raw.IndexOf("\"slug\"", StringComparison.Ordinal),
            "properties come in a fixed order");
    }

    [Fact]
    public async Task Export_WritesTheHashtagsOfCategoriesAndTags_AndNullWhenThereAreNone()
    {
        await using (var db = _database.CreateContext())
        {
            (await db.Categories.SingleAsync()).Hashtags = [new("Homelab", HashtagNetworks.Both), new("SelfHosted", HashtagNetworks.BlueskyOnly)];
            await db.SaveChangesAsync();
        }

        var output = Path.Combine(NewParent(), "content");
        await ExportAsync(output);

        Deserialize<List<CategoryEntry>>(output, "categories.json").Should().ContainSingle().Which.Hashtags.Should().Equal(
            [new HashtagDto("Homelab", HashtagNetworks.Both), new HashtagDto("SelfHosted", HashtagNetworks.BlueskyOnly)]);
        var tags = Deserialize<List<TagEntry>>(output, "tags.json");
        tags.Should().HaveCount(2).And.OnlyContain(tag => tag.Hashtags == null, "a tag without hashtags exports null, not an empty list");
        System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(Path.Combine(output, "tags.json")), "\"hashtags\": null").Should().HaveCount(2,
            "the file says null for each tag");
    }

    [Fact]
    public async Task Export_WritesCategoriesTagsAndMediaWithEveryField()
    {
        var output = Path.Combine(NewParent(), "content");
        await ExportAsync(output);

        Deserialize<List<CategoryEntry>>(output, "categories.json").Should().Equal(
            new CategoryEntry(Guid.Parse("00000000-0000-0000-0000-0000000000c1"), "Engineering", "engineering", "build"));
        Deserialize<List<TagEntry>>(output, "tags.json").Should().Equal(
            new TagEntry(Guid.Parse("00000000-0000-0000-0000-0000000000d2"), "AI assisted", "ai-assisted", false),
            new TagEntry(Guid.Parse("00000000-0000-0000-0000-0000000000d1"), "Dotnet", "dotnet", true));

        var media = Deserialize<List<MediaEntry>>(output, "media.json");
        media.Select(item => item.Id.ToString()[^2..]).Should().Equal("e1", "e2", "e3");
        media[1].Should().Be(new MediaEntry(
            Guid.Parse("00000000-0000-0000-0000-0000000000e2"), "diagram.svg", "orig-diagram.svg", "image/svg+xml",
            "Diagram of the thing", "posts/e2.svg", ContentSeed.At(11, 888888), "admin-id", MediaAssetScope.PostSpecific,
            ContentSeed.PublishedId));
        media[2].AltText.Should().BeNull();
    }

    [Fact]
    public async Task TwoExportsOfUnchangedContent_AreByteIdentical_ExceptTheManifestsExportedAt()
    {
        var first = Path.Combine(NewParent(), "content");
        var second = Path.Combine(NewParent(), "content");

        await ExportAsync(first);
        await using (var db = _database.CreateContext())
            await ContentExporter.ExportAsync(db, second, ExportedAt.AddHours(5), "9.9.9-test");

        var firstFiles = Directory.GetFiles(first, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(first, path)).Order().ToList();
        var secondFiles = Directory.GetFiles(second, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(second, path)).Order().ToList();
        secondFiles.Should().Equal(firstFiles);
        foreach (var file in firstFiles.Where(file => file != "manifest.json"))
        {
            (await File.ReadAllBytesAsync(Path.Combine(second, file))).Should().Equal(await File.ReadAllBytesAsync(Path.Combine(first, file)), file);
        }

        var firstManifest = Deserialize<ManifestFile>(first, "manifest.json");
        var secondManifest = Deserialize<ManifestFile>(second, "manifest.json");
        secondManifest.ExportedAt.Should().NotBe(firstManifest.ExportedAt);
        (secondManifest with { ExportedAt = firstManifest.ExportedAt }).Should().Be(firstManifest);
    }

    [Fact]
    public async Task ExportingOverAnExistingDirectory_ReplacesItWhole_AndLeavesNoTemporaryFolders()
    {
        var parent = NewParent();
        var output = Path.Combine(parent, "content");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "stale.txt"), "from the previous export");

        await ExportAsync(output);

        File.Exists(Path.Combine(output, "stale.txt")).Should().BeFalse("the directory is replaced, not merged into");
        File.Exists(Path.Combine(output, "manifest.json")).Should().BeTrue();
        Directory.GetDirectories(parent).Should().ContainSingle().Which.Should().Be(output, "no .tmp-* or .old-* folder is left behind");
    }

    [Fact]
    public async Task AnExport_SweepsStaleTmpAndOldFoldersLeftByACrashedRun()
    {
        var parent = NewParent();
        var output = Path.Combine(parent, "content");
        Directory.CreateDirectory(output);
        foreach (var stale in new[] { output + ".tmp-0123", output + ".old-4567" })
        {
            Directory.CreateDirectory(stale);
            await File.WriteAllTextAsync(Path.Combine(stale, "leftover.txt"), "from a crashed run");
        }
        Directory.CreateDirectory(Path.Combine(parent, "content-other.tmp-keep"));

        await ExportAsync(output);

        Directory.GetDirectories(parent).Select(Path.GetFileName).Order().Should().Equal("content", "content-other.tmp-keep");
    }

    [Fact]
    public async Task AnExportThatFails_RestoresThePreviousExportFromOldWhenACrashLeftNoTargetFolder()
    {
        // A crash between the swap's two renames leaves the good export only as <dir>.old-*.
        var parent = NewParent();
        var output = Path.Combine(parent, "content");
        Directory.CreateDirectory(output + ".old-4567");
        await File.WriteAllTextAsync(Path.Combine(output + ".old-4567", "previous-good-export.txt"), "keep me");
        Directory.CreateDirectory(output + ".tmp-0123");

        var exitCode = await ExportToUnreachableDatabaseAsync(output);

        exitCode.Should().Be(1);
        File.ReadAllText(Path.Combine(output, "previous-good-export.txt")).Should().Be("keep me");
        Directory.GetDirectories(parent).Should().ContainSingle().Which.Should().Be(output);
    }

    [Fact]
    public async Task Export_WorksOnAReadOnlyConnection_LikeAReadReplica()
    {
        var output = Path.Combine(NewParent(), "content");

        var manifest = await ExportAsync(output, connectionOptions: "-c default_transaction_read_only=on");

        manifest.Counts.Posts.Should().Be(2);
    }

    [Fact]
    public async Task Export_ReadsAConsistentSnapshot_IgnoringRowsCommittedWhileItRuns()
    {
        // The posts are read first and the tags later, in one transaction. A tag renamed and
        // committed between those reads must not show: the files agree with each other.
        var output = Path.Combine(NewParent(), "content");
        var injected = false;
        await using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>()
            .UseNpgsql(_database.ConnectionString)
            .AddInterceptors(new AfterFirstReaderCommandInterceptor(async () =>
            {
                if (injected)
                    return;
                injected = true;
                await using var other = _database.CreateContext();
                await other.Database.ExecuteSqlRawAsync(
                    """UPDATE "Tags" SET "Name" = 'changed while exporting' WHERE "Slug" = 'dotnet'""");
            }))
            .Options);

        await ContentExporter.ExportAsync(db, output, ExportedAt, "9.9.9-test");

        injected.Should().BeTrue("the concurrent write really happened during the export");
        Deserialize<List<TagEntry>>(output, "tags.json").Should().ContainSingle(tag => tag.Slug == "dotnet")
            .Which.Name.Should().Be("Dotnet");
    }

    [Fact]
    public async Task UnreachableDatabase_ExitsWithCodeOneWithinTheMigrateTimeout_AndTheExistingExportIsKept()
    {
        var parent = NewParent();
        var output = Path.Combine(parent, "content");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "previous-good-export.txt"), "keep me");

        var started = TimeProvider.System.GetTimestamp();
        var (exitCode, logs) = await ExportToUnreachableDatabaseWithLogsAsync(output);
        var elapsed = TimeProvider.System.GetElapsedTime(started);

        exitCode.Should().Be(1);
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "it gives up at the migrate timeout instead of hanging");
        logs.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error);
        File.ReadAllText(Path.Combine(output, "previous-good-export.txt")).Should().Be("keep me");
        Directory.GetDirectories(parent).Should().ContainSingle().Which.Should().Be(output, "no half-written temporary folder either");
    }

    /// <summary>Runs <paramref name="afterFirstRead"/> once, right after the first query has executed.</summary>
    private sealed class AfterFirstReaderCommandInterceptor(Func<Task> afterFirstRead) : DbCommandInterceptor
    {
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            await afterFirstRead();
            return result;
        }
    }

    private static async Task<int> ExportToUnreachableDatabaseAsync(string output) =>
        (await ExportToUnreachableDatabaseWithLogsAsync(output)).ExitCode;

    private static async Task<(int ExitCode, LogCollector Logs)> ExportToUnreachableDatabaseWithLogsAsync(string output)
    {
        var services = new ServiceCollection()
            .AddDbContext<BlogDbContext>(options => options.UseNpgsql("Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p;Timeout=2"))
            .BuildServiceProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:MigrateTimeout"] = "00:00:02" }).Build();
        var logs = new LogCollector();
        var exitCode = await ContentCommands.ExportAsync(
            services, configuration, logs.CreateLogger("test"), TimeProvider.System, output);
        return (exitCode, logs);
    }

    private static T Deserialize<T>(string directory, string relativePath) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(directory, relativePath)), ContentFormat.Json)!;
}
