using System.Reflection;
using System.Text.Json;
using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Api.Services.ContentExport;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts.Social;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NpgsqlTypes;

namespace Croft.Api.Tests;

/// <summary>
/// The content import and the round trip (brief 11c item 2). The round trip is the test that
/// proves nothing is lost: seed, export A, import A into a fresh database, export B, A equals B.
/// </summary>
public sealed class ContentImportTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset ExportedAt = new(2026, 10, 19, 3, 0, 0, TimeSpan.Zero);

    private readonly List<string> _directories = [];
    private readonly List<ScratchDatabase> _databases = [];
    private ScratchDatabase _source = null!;

    public async ValueTask InitializeAsync()
    {
        _source = await NewDatabaseAsync();
        await using var db = _source.CreateContext();
        await ContentSeed.SeedAsync(db);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var database in _databases)
            await database.DisposeAsync();
        foreach (var directory in _directories.Where(Directory.Exists))
            Directory.Delete(directory, recursive: true);
    }

    private async Task<ScratchDatabase> NewDatabaseAsync()
    {
        var database = await ScratchDatabase.CreateAsync(fixture.ConnectionString);
        _databases.Add(database);
        return database;
    }

    private string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "croft-import-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        return directory;
    }

    private async Task<string> ExportAsync(ScratchDatabase database, DateTimeOffset? at = null)
    {
        var output = Path.Combine(NewDirectory(), "content");
        await using var db = database.CreateContext();
        await ContentExporter.ExportAsync(db, output, at ?? ExportedAt, "9.9.9-test");
        return output;
    }

    /// <summary>Runs the real command entry (as "import --from" does) and returns its exit code and log.</summary>
    private static async Task<(int ExitCode, LogCollector Logs)> RunImportAsync(
        ScratchDatabase target, string from, string? mediaRoot = null)
    {
        var services = new ServiceCollection()
            .AddDbContext<BlogDbContext>(options => options.UseNpgsql(target.ConnectionString))
            .BuildServiceProvider();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateTimeout"] = "00:00:05",
                ["FileStorage:BasePath"] = mediaRoot ?? Path.Combine(Path.GetTempPath(), "croft-no-media-" + Guid.NewGuid().ToString("N"))
            }).Build();
        var logs = new LogCollector();
        var exitCode = await ContentCommands.ImportAsync(
            services, configuration, logs.CreateLogger("test"), TimeProvider.System, from);
        return (exitCode, logs);
    }

    private static async Task<int[]> CountsAsync(ScratchDatabase database)
    {
        await using var db = database.CreateContext();
        return
        [
            await db.Posts.CountAsync(), await db.Categories.CountAsync(), await db.Tags.CountAsync(),
            await db.MediaAssets.CountAsync(), await db.Feedback.CountAsync()
        ];
    }

    [Fact]
    public async Task RoundTrip_ExportThenImportIntoAFreshDatabaseThenExport_GivesIdenticalFiles()
    {
        var exportA = await ExportAsync(_source);
        var target = await NewDatabaseAsync();

        var (exitCode, _) = await RunImportAsync(target, exportA);
        var exportB = await ExportAsync(target, ExportedAt.AddDays(1));

        exitCode.Should().Be(0);
        var filesA = Files(exportA);
        Files(exportB).Should().Equal(filesA, "the same files exist");
        foreach (var file in filesA.Where(file => file != "manifest.json"))
        {
            (await File.ReadAllBytesAsync(Path.Combine(exportB, file))).Should().Equal(await File.ReadAllBytesAsync(Path.Combine(exportA, file)), file);
        }

        var manifestA = JsonSerializer.Deserialize<ManifestFile>(await File.ReadAllTextAsync(Path.Combine(exportA, "manifest.json")), ContentFormat.Json)!;
        var manifestB = JsonSerializer.Deserialize<ManifestFile>(await File.ReadAllTextAsync(Path.Combine(exportB, "manifest.json")), ContentFormat.Json)!;
        (manifestB with { ExportedAt = manifestA.ExportedAt }).Should().Be(manifestA, "only exportedAt differs");
    }

    [Fact]
    public async Task RoundTrip_KeepsTheHashtagsOfCategoriesAndTags_InOrder()
    {
        await using (var db = _source.CreateContext())
        {
            (await db.Categories.SingleAsync()).Hashtags = [new("Homelab", HashtagNetworks.Both), new("SelfHosted", HashtagNetworks.BlueskyOnly)];
            (await db.Tags.SingleAsync(t => t.Slug == "dotnet")).Hashtags = [new("DotNet", HashtagNetworks.MastodonOnly)];
            await db.SaveChangesAsync();
        }

        var target = await NewDatabaseAsync();
        (await RunImportAsync(target, await ExportAsync(_source))).ExitCode.Should().Be(0);

        await using var restored = target.CreateContext();
        (await restored.Categories.AsNoTracking().SingleAsync()).Hashtags
            .Should().Equal([new("Homelab", HashtagNetworks.Both), new("SelfHosted", HashtagNetworks.BlueskyOnly)]);
        (await restored.Tags.AsNoTracking().SingleAsync(t => t.Slug == "dotnet")).Hashtags
            .Should().Equal([new("DotNet", HashtagNetworks.MastodonOnly)]);
        (await restored.Tags.AsNoTracking().SingleAsync(t => t.Slug == "ai-assisted")).Hashtags.Should().BeEmpty();
    }

    [Fact]
    public async Task Import_OfAnArchiveWrittenBeforeHashtagsExisted_GivesEmptyLists()
    {
        var export = await ExportAsync(_source);
        foreach (var file in new[] { "categories.json", "tags.json" })
        {
            var path = Path.Combine(export, file);
            var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsArray();
            foreach (var entry in json)
                entry!.AsObject().Remove("hashtags");
            await File.WriteAllTextAsync(path, json.ToJsonString());
        }

        var target = await NewDatabaseAsync();
        (await RunImportAsync(target, export)).ExitCode.Should().Be(0);

        await using var restored = target.CreateContext();
        (await restored.Categories.AsNoTracking().SingleAsync()).Hashtags.Should().BeEmpty();
        (await restored.Tags.AsNoTracking().ToListAsync()).Should().OnlyContain(t => t.Hashtags.Count == 0);
    }

    [Fact]
    public async Task Import_WithAnInvalidHashtag_ExitsWithOneWithTheApisMessage_AndTheDatabaseStaysEmpty()
    {
        var export = await ExportAsync(_source);
        var path = Path.Combine(export, "categories.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsArray();
        json[0]!.AsObject()["hashtags"] = System.Text.Json.Nodes.JsonNode.Parse("""[{"name":"two words","networks":"both"}]""");
        await File.WriteAllTextAsync(path, json.ToJsonString());
        var target = await NewDatabaseAsync();

        var (exitCode, logs) = await RunImportAsync(target, export);

        exitCode.Should().Be(1);
        logs.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error)
            .Which.Message.Should().Contain("Hashtags: '#two words' may contain only letters, digits and underscores.");
        (await CountsAsync(target)).Should().Equal(0, 0, 0, 0, 0);
    }

    [Fact]
    public async Task Import_TellsRunningInstancesOnCommit_SoTheirCachedEmptySiteDoesNotLinger()
    {
        var exportA = await ExportAsync(_source);
        var target = await NewDatabaseAsync();
        await using var listener = await NotificationListener.StartAsync(target.ConnectionString, Croft.Api.Services.CacheNotifier.Channel);

        (await RunImportAsync(target, exportA)).ExitCode.Should().Be(0);

        var received = await listener.WaitForSentinelAsync();
        received.Should().ContainSingle("the import sends one cache NOTIFY, delivered with its commit")
            .Which.Split(',').Should().BeEquivalentTo(["categories", "tags", "posts", "pages"]);
    }

    [Fact]
    public async Task RoundTrip_RestoresEveryColumnOfEveryRow_AndTheRelations_AsTheSourceDatabaseHadThem()
    {
        // The files could agree with each other and both be missing a field the source row had; so the
        // restored rows are compared with the source database's own rows, property by property.
        var exportA = await ExportAsync(_source);
        var target = await NewDatabaseAsync();
        (await RunImportAsync(target, exportA)).ExitCode.Should().Be(0);

        await using var source = _source.CreateContext();
        await using var restored = target.CreateContext();

        foreach (var set in new Func<BlogDbContext, IQueryable<object>>[]
                 {
                     db => db.Posts.AsNoTracking().OrderBy(x => x.Id), db => db.Categories.AsNoTracking().OrderBy(x => x.Id),
                     db => db.Tags.AsNoTracking().OrderBy(x => x.Id), db => db.MediaAssets.AsNoTracking().OrderBy(x => x.Id),
                     db => db.Feedback.AsNoTracking().OrderBy(x => x.Id)
                 })
        {
            var expected = await set(source).ToListAsync();
            var actual = await set(restored).ToListAsync();
            expected.Should().NotBeEmpty();
            actual.Select(Scalars).Should().BeEquivalentTo(expected.Select(Scalars), options => options.WithStrictOrdering());
        }

        var relations = async (BlogDbContext db) => (await db.Posts.AsNoTracking().Include(p => p.Tags).OrderBy(p => p.Id).ToListAsync())
            .Select(p => $"{p.Id}:{string.Join(',', p.Tags.Select(t => t.Id).Order())}").ToList();
        (await relations(restored)).Should().Equal(await relations(source));
    }

    [Fact]
    public async Task Import_RebuildsTheSearchVector_SoSearchFindsTheRestoredPosts()
    {
        var target = await NewDatabaseAsync();
        (await RunImportAsync(target, await ExportAsync(_source))).ExitCode.Should().Be(0);

        await using var db = target.CreateContext();
        var found = await db.Posts.Where(post => post.SearchVector.Matches(EF.Functions.PlainToTsQuery("english", "hello croft")))
            .Select(post => post.Slug).ToListAsync();

        found.Should().Contain("hello-croft");
        (await db.Posts.Where(post => post.Slug == "hello-croft").Select(post => post.SearchVector).SingleAsync())
            .Should().NotBeNull().And.Match<NpgsqlTsVector>(vector => vector.ToString().Contains("croft"));
    }

    [Fact]
    public async Task Import_IntoADatabaseWithAPost_ExitsWithOne_NamingTheProblem_AndChangesNothing()
    {
        var target = await NewDatabaseAsync();
        await using (var db = target.CreateContext())
        {
            db.Posts.Add(new Post { Id = Guid.NewGuid(), Slug = "already-here", Title = "Already here", MarkdownContent = "x", AuthorId = "a", AuthorName = "A" });
            await db.SaveChangesAsync();
        }

        var (exitCode, logs) = await RunImportAsync(target, await ExportAsync(_source));

        exitCode.Should().Be(1);
        logs.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error)
            .Which.Message.Should().Contain("already has content").And.Contain("never merges");
        (await CountsAsync(target)).Should().Equal(1, 0, 0, 0, 0);
        await using var check = target.CreateContext();
        (await check.Posts.SingleAsync()).Slug.Should().Be("already-here");
    }

    [Fact]
    public async Task Import_IntoADatabaseWithOnlyATag_AlsoRefuses()
    {
        var target = await NewDatabaseAsync();
        await using (var db = target.CreateContext())
        {
            db.Tags.Add(new Tag { Id = Guid.NewGuid(), Name = "Lonely", Slug = "lonely" });
            await db.SaveChangesAsync();
        }

        (await RunImportAsync(target, await ExportAsync(_source))).ExitCode.Should().Be(1);
        (await CountsAsync(target)).Should().Equal(0, 0, 1, 0, 0);
    }

    [Fact]
    public async Task Import_WithAMarkdownFileMissing_ExitsWithOneNamingIt_AndTheDatabaseStaysEmpty()
    {
        var export = await ExportAsync(_source);
        File.Delete(Path.Combine(export, "posts", "hello-croft.md"));
        var target = await NewDatabaseAsync();

        var (exitCode, logs) = await RunImportAsync(target, export);

        exitCode.Should().Be(1);
        logs.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error)
            .Which.Message.Should().Contain("hello-croft.md");
        (await CountsAsync(target)).Should().Equal(0, 0, 0, 0, 0);
    }

    [Fact]
    public async Task Import_WhereMarkdownFileNamesAnotherFile_ExitsWithOneNamingIt_AndTheDatabaseStaysEmpty()
    {
        var export = await ExportAsync(_source);
        var path = Path.Combine(export, "posts", "work-in-progress.json");
        await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("\"markdownFile\": \"work-in-progress.md\"", "\"markdownFile\": \"hello-croft.md\""));
        var target = await NewDatabaseAsync();

        var (exitCode, logs) = await RunImportAsync(target, export);

        exitCode.Should().Be(1);
        logs.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error)
            .Which.Message.Should().Contain("hello-croft.md").And.Contain("work-in-progress");
        (await CountsAsync(target)).Should().Equal(0, 0, 0, 0, 0);
    }

    [Fact]
    public async Task Import_WithFormatVersionTwo_ExitsWithOne_AndTheDatabaseStaysEmpty()
    {
        var export = await ExportAsync(_source);
        var path = Path.Combine(export, "manifest.json");
        await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("\"formatVersion\": 1", "\"formatVersion\": 2"));
        var target = await NewDatabaseAsync();

        var (exitCode, logs) = await RunImportAsync(target, export);

        exitCode.Should().Be(1);
        logs.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error)
            .Which.Message.Should().Contain("formatVersion 2");
        (await CountsAsync(target)).Should().Equal(0, 0, 0, 0, 0);
    }

    [Fact]
    public async Task Import_WithAPostJsonDeleted_ExitsWithOne_BecauseTheManifestCountsDoNotMatch()
    {
        var export = await ExportAsync(_source);
        File.Delete(Path.Combine(export, "posts", "work-in-progress.json"));
        File.Delete(Path.Combine(export, "posts", "work-in-progress.md"));
        var target = await NewDatabaseAsync();

        var (exitCode, logs) = await RunImportAsync(target, export);

        exitCode.Should().Be(1);
        logs.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error).Which.Message.Should().Contain("posts");
        (await CountsAsync(target)).Should().Equal(0, 0, 0, 0, 0);
    }

    [Fact]
    public async Task Import_ThatFailsInTheDatabaseAfterTheFirstWrites_RollsEverythingBack()
    {
        // A page with a post's slug passes every file check, then hits the unique slug index
        // after categories, tags and media were already written in the same transaction.
        var export = await ExportAsync(_source);
        var page = await File.ReadAllTextAsync(Path.Combine(export, "pages", "about.json"));
        await File.WriteAllTextAsync(Path.Combine(export, "pages", "hello-croft.json"),
            page.Replace("\"slug\": \"about\"", "\"slug\": \"hello-croft\"").Replace("\"markdownFile\": \"about.md\"", "\"markdownFile\": \"hello-croft.md\""));
        File.Copy(Path.Combine(export, "pages", "about.md"), Path.Combine(export, "pages", "hello-croft.md"));
        File.Delete(Path.Combine(export, "pages", "about.json"));
        File.Delete(Path.Combine(export, "pages", "about.md"));
        var target = await NewDatabaseAsync();

        var (exitCode, logs) = await RunImportAsync(target, export);

        exitCode.Should().Be(1);
        logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Error);
        (await CountsAsync(target)).Should().Equal(0, 0, 0, 0, 0);
    }

    [Fact]
    public async Task Import_LogsAWarningPerMediaFileMissingUnderTheMediaRoot_AndStillSucceeds()
    {
        var mediaRoot = NewDirectory();
        Directory.CreateDirectory(Path.Combine(mediaRoot, "banners"));
        await File.WriteAllTextAsync(Path.Combine(mediaRoot, "banners", "e1.png"), "present");
        var target = await NewDatabaseAsync();

        var (exitCode, logs) = await RunImportAsync(target, await ExportAsync(_source), mediaRoot);

        exitCode.Should().Be(0);
        var warnings = logs.Entries.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message).ToList();
        warnings.Should().HaveCount(2);
        warnings.Should().ContainSingle(message => message.Contains("posts/e2.svg"));
        warnings.Should().ContainSingle(message => message.Contains("spare/e3.jpg"));
        (await CountsAsync(target))[3].Should().Be(3, "media rows are restored as metadata either way");
    }

    [Fact]
    public async Task Import_WherePostMediaAssetIdsDisagreeWithMediaJson_ExitsWithOneNamingThePost_AndWritesNothing()
    {
        // Two files state the same relation; if they disagree, one of them was edited or damaged.
        var export = await ExportAsync(_source);
        var path = Path.Combine(export, "posts", "hello-croft.json");
        await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace(
            "\"mediaAssetIds\": [\n    \"00000000-0000-0000-0000-0000000000e2\"\n  ]",
            "\"mediaAssetIds\": [\n    \"00000000-0000-0000-0000-0000000000e2\",\n    \"00000000-0000-0000-0000-0000000000e3\"\n  ]"));
        (await File.ReadAllTextAsync(path)).Should().Contain("0000000000e3", "the test really edited the file");
        var target = await NewDatabaseAsync();

        var (exitCode, logs) = await RunImportAsync(target, export);

        exitCode.Should().Be(1);
        logs.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error)
            .Which.Message.Should().Contain("hello-croft").And.Contain("mediaAssetIds");
        (await CountsAsync(target)).Should().Equal(0, 0, 0, 0, 0);
    }

    [Fact]
    public async Task Import_WhereAPostsBannerIsNotInMediaJson_ExitsWithOneNamingThePost_AndWritesNothing()
    {
        // The media lookup the importer builds must still refuse a reference to an asset the archive lacks.
        var export = await ExportAsync(_source);
        var path = Path.Combine(export, "posts", "hello-croft.json");
        var text = await File.ReadAllTextAsync(path);
        const string Banner = "00000000-0000-0000-0000-0000000000e1";
        text.Should().Contain(Banner, "the seeded post uses the e1 banner");
        await File.WriteAllTextAsync(path, text.Replace("\"bannerImageId\": \"" + Banner + "\"", "\"bannerImageId\": \"00000000-0000-0000-0000-0000000000ff\""));
        (await File.ReadAllTextAsync(path)).Should().Contain("0000000000ff", "the test really edited the file");
        var target = await NewDatabaseAsync();

        var (exitCode, logs) = await RunImportAsync(target, export);

        exitCode.Should().Be(1);
        logs.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error)
            .Which.Message.Should().Contain("hello-croft").And.Contain("banner image is not in media.json");
        (await CountsAsync(target)).Should().Equal(0, 0, 0, 0, 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\0root")]
    public async Task Import_WithAnEmptyOrInvalidMediaRoot_ExitsWithOneBeforeWritingAnything(string mediaRoot)
    {
        var target = await NewDatabaseAsync();

        var (exitCode, logs) = await RunImportAsync(target, await ExportAsync(_source), mediaRoot);

        exitCode.Should().Be(1);
        logs.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error)
            .Which.Message.Should().Contain("FileStorage:BasePath");
        (await CountsAsync(target)).Should().Equal(0, 0, 0, 0, 0);
    }

    [Fact]
    public async Task Import_WhereAMediaFilePathHoldsANulCharacter_ExitsWithOneAndWritesNothing()
    {
        // A NUL in a hand-edited FilePath makes the path check throw (ArgumentException); Postgres
        // refuses the text first (22021), so the whole import rolls back instead of failing after a commit.
        var export = await ExportAsync(_source);
        var mediaJson = Path.Combine(export, "media.json");
        await File.WriteAllTextAsync(mediaJson, (await File.ReadAllTextAsync(mediaJson)).Replace("posts/e2.svg", "posts/e2\\u0000.svg"));
        var target = await NewDatabaseAsync();

        var (exitCode, _) = await RunImportAsync(target, export);

        exitCode.Should().Be(1);
        (await CountsAsync(target)).Should().Equal(0, 0, 0, 0, 0);
    }

    private static List<string> Files(string directory) =>
        [.. Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path)).Order(StringComparer.Ordinal)];

    /// <summary>Every public, settable, non-navigation property of the row, by name, except the derived SearchVector.</summary>
    private static Dictionary<string, object?> Scalars(object row) =>
        row.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.SetMethod is { IsPublic: true } && property.Name != nameof(Post.SearchVector)
                && (property.PropertyType.IsValueType || property.PropertyType == typeof(string)
                    || Nullable.GetUnderlyingType(property.PropertyType) is not null))
            .ToDictionary(property => property.Name, property => property.GetValue(row));
}
