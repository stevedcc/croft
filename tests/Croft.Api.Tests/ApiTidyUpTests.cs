using AwesomeAssertions;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Croft.Api.Data;
using Croft.Api.Services;
using Croft.Api.Tests.Fixtures;
using Croft.Api.Tests.Fixtures.Social;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;

namespace Croft.Api.Tests;

/// <summary>Brief 13i item 1: the API service tidy-ups (M-4, M-7, M-8, M-9).</summary>
public sealed class ApiTidyUpTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Utc)]
    public async Task Publish_NormalisesPublishedAtWithTheInjectedNormaliser(DateTimeKind kind)
    {
        // M-7: the instant PublishAsync stores is the DateTimeNormalizer's, not an inline copy of its rules.
        await using var factory = CreateFactory();
        var admin = factory.CreateClient().WithRole("Admin");
        var created = (await (await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Normaliser " + kind, "body")))
            .Content.ReadFromJsonAsync<PostDto>())!;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var normalizer = new DateTimeNormalizer(time);
        var requested = new DateTime(2030, 1, 1, 12, 0, 0, kind);

        await using var scope = factory.Services.CreateAsyncScope();
        var postsDb = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
        var posts = new PostService(
            postsDb, new SlugService(), null!,
            NullLogger<PostService>.Instance, time, new ScheduledPublishWakeup(), TestCache.NewNotifier(),
            TestCache.NewCache(), new HttpContextAccessor(), normalizer, new NoSocialLinks(), NoSocialLinks.Options, NoSocialLinks.Outbox(postsDb));
        var published = await posts.PublishAsync(created.Id, new PublishRequest(requested));

        published!.Status.Should().Be(PostStatus.Scheduled);
        published.PublishedAt.Should().Be(normalizer.NormalizeToUtc(requested));
    }

    [Fact]
    public async Task CreateAndUpdate_WithFiveTags_ResolveThemInOneTagSelect()
    {
        // M-8 (first half): one query for the matching tags, not one per tag name.
        var signal = new TagResolutionCounter();
        await using var factory = CreateFactory(signal);
        var admin = factory.CreateClient().WithRole("Admin");
        var tags = new[] { "tidy-one", "tidy-two", "tidy-three", "tidy-four", "tidy-five" };

        var before = signal.Count;
        var create = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Five tags", "body", Tags: tags));
        create.EnsureSuccessStatusCode();
        var created = (await create.Content.ReadFromJsonAsync<PostDto>())!;
        var createSelects = signal.Count - before;

        before = signal.Count;
        var update = await admin.PutAsJsonAsync($"/api/posts/{created.Id}", new UpdatePostRequest("Five tags", "body", Tags: tags));
        update.EnsureSuccessStatusCode();
        var updateSelects = signal.Count - before;

        createSelects.Should().Be(1, "creating a post with five tags reads the tags once");
        updateSelects.Should().Be(1, "updating it reads the tags once");
        (await update.Content.ReadFromJsonAsync<PostDto>())!.Tags.Select(t => t.Slug).Should().BeEquivalentTo(tags);
    }

    [Fact]
    public async Task Upload_9MbPng_HandsStorageTheUploadedBytes_WithoutASecondMemoryCopy()
    {
        // M-4: the upload streams to storage; the saved bytes equal the uploaded bytes.
        var storage = new RecordingStorage();
        await using var factory = CreateFactory(configureServices: services =>
        {
            services.RemoveAll<IFileStorage>();
            services.AddSingleton<IFileStorage>(storage);
        });
        var admin = factory.CreateClient().WithRole("Admin");
        var pngHeader = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var nineMb = new byte[9 * 1024 * 1024];
        new Random(13).NextBytes(nineMb);
        Array.Copy(pngHeader, nineMb, pngHeader.Length);
        var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(nineMb) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } }, "file", "nine.png" },
            { new StringContent("global"), "scope" }
        };

        var upload = await admin.PostAsync("/api/media", content);

        upload.EnsureSuccessStatusCode();
        storage.Saved.Should().Equal(nineMb);
        storage.StreamType.Should().NotBe<MemoryStream>( "the service must not copy the upload into a second buffer");
    }

    private BlogApiFactory CreateFactory(Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor? interceptor = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        return new BlogApiFactory(fixture.ConnectionString, storagePath,
            configureServices: services =>
            {
                services.RemoveAll<IHostedService>();
                configureServices?.Invoke(services);
            },
            commandInterceptor: interceptor);
    }

    /// <summary>Counts the reads that resolve tags by slug: they select the Tags table with its Slug column.
    /// The per-tag post counts of the response read Tags too, but never the Slug.</summary>
    private sealed class TagResolutionCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"Tags\"", StringComparison.Ordinal)
                && command.CommandText.Contains("\"Slug\"", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _count);
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class RecordingStorage : IFileStorage
    {
        public byte[] Saved { get; private set; } = [];
        public Type? StreamType { get; private set; }

        public async Task<string> SaveAsync(Stream data, string fileName, string contentType, string storagePath, CancellationToken ct = default)
        {
            StreamType = data.GetType();
            using var copy = new MemoryStream();
            await data.CopyToAsync(copy, ct);
            Saved = copy.ToArray();
            return $"{storagePath}/{Guid.NewGuid():N}{Path.GetExtension(fileName)}";
        }

        public Stream OpenRead(string relativePath) => throw new NotSupportedException();

        public void Delete(string relativePath)
        {
        }
    }
}
