using System.Data.Common;
using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Api.Services;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Net.Http.Json;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 13c item 3 (architecture finding 3, .NET review I-1 and M-3): a content write and its
/// cache invalidation are one event. SaveChanges and pg_notify run in one transaction, so Postgres
/// delivers the NOTIFY when the write commits and only then; a NOTIFY that fails takes the write
/// with it; one write sends one NOTIFY; and nothing after the commit is cancelled by the request.
/// </summary>
public class CacheInvalidationTransactionTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly string[] AllGroups = ["categories", "tags", "posts", "pages"];

    [Fact]
    public async Task APostSave_CommitsAndSendsExactlyOneNotification_NamingEveryGroup()
    {
        await using var listener = await NotificationListener.StartAsync(fixture.ConnectionString, CacheNotifier.Channel);
        await using var factory = CreateFactory();
        var admin = factory.CreateClient().WithRole("Admin");

        var created = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Notify Once Post", "## Body"));
        created.EnsureSuccessStatusCode();
        var post = (await created.Content.ReadFromJsonAsync<PostDto>())!;

        var received = await listener.WaitForSentinelAsync();
        received.Should().ContainSingle("one write sends one NOTIFY, not one per cache group");
        received[0].Split(',').Should().BeEquivalentTo(AllGroups, "the one payload names every group the write invalidates");
        (await admin.GetAsync($"/api/posts/{post.Id}")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ANotifyThatFails_RollsTheWriteBack_AndTheRequestFails()
    {
        var interceptor = new FailNotifyInterceptor();
        await using var factory = CreateFactory(interceptor);
        var admin = factory.CreateClient().WithRole("Admin");
        interceptor.Armed = true;

        var response = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("Notify Failure Post", "## Body"));

        response.IsSuccessStatusCode.Should().BeFalse("the write fails with its notification");
        interceptor.Armed = false;
        var list = await admin.GetFromJsonAsync<PagedResult<PostSummaryDto>>("/api/posts?searchTerm=Notify+Failure&pageSize=50");
        list!.Items.Should().NotContain(p => p.Title == "Notify Failure Post",
            "a write whose notification could not be sent did not happen: no other instance would hear of it");
    }

    [Fact]
    public async Task AWriteWhoseCommitFails_SendsNoNotification_AndLeavesNoRow()
    {
        var slug = $"commit-failure-{Guid.NewGuid():N}";
        await using var listener = await NotificationListener.StartAsync(fixture.ConnectionString, CacheNotifier.Channel);
        var interceptor = new FailCommitInterceptor();
        var notifier = new CacheNotifier(new MemoryPublicReadCache(new MemoryCache(new MemoryCacheOptions())));
        await using (var db = NewContext(interceptor))
        {
            db.Tags.Add(new Tag { Name = "Commit Failure", Slug = slug });

            var act = () => notifier.SaveAndNotifyAsync(db, CacheGroup.All);
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("commit failed (test)");
        }

        var received = await listener.WaitForSentinelAsync();
        received.Should().BeEmpty("the NOTIFY is delivered on commit, and this write never committed");
        await using var check = NewContext();
        (await check.Tags.AnyAsync(t => t.Slug == slug)).Should().BeFalse("the failed commit left no row");
    }

    [Fact]
    public async Task CancellingTheRequestRightAfterTheCommit_StillInvalidatesTheLocalCache()
    {
        using var cancel = new CancellationTokenSource();
        var cache = new MemoryPublicReadCache(new MemoryCache(new MemoryCacheOptions()));
        var notifier = new CacheNotifier(cache);
        await using var db = NewContext(new CancelAfterCommitInterceptor(cancel));

        var cached = await cache.GetOrCreateAsync(CacheGroup.Tags, "tags:cancel-after-commit", _ => Task.FromResult("before"));
        cached.Should().Be("before");
        db.Tags.Add(new Tag { Name = "Cancel After Commit", Slug = $"cancel-after-commit-{Guid.NewGuid():N}" });

        await notifier.SaveAndNotifyAsync(db, CacheGroup.All, cancel.Token);

        cancel.IsCancellationRequested.Should().BeTrue("the request was cancelled at the commit");
        var after = await cache.GetOrCreateAsync(CacheGroup.Tags, "tags:cancel-after-commit", _ => Task.FromResult("after"));
        after.Should().Be("after", "a closed tab must not leave this instance's cache stale once the write committed");
    }

    private BlogDbContext NewContext(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(interceptors).Options);

    private BlogApiFactory CreateFactory(DbCommandInterceptor? interceptor = null)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        return new BlogApiFactory(
            fixture.ConnectionString,
            storagePath,
            configureServices: services =>
            {
                services.RemoveAll<IHostedService>();
            },
            commandInterceptor: interceptor);
    }

    private sealed class FailNotifyInterceptor : DbCommandInterceptor
    {
        public volatile bool Armed;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            Fail(command) ? throw new InvalidOperationException("pg_notify failed (test)") : ValueTask.FromResult(result);

        private bool Fail(DbCommand command) => Armed && command.CommandText.Contains("pg_notify", StringComparison.Ordinal);
    }

    private sealed class FailCommitInterceptor : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("commit failed (test)");
    }

    private sealed class CancelAfterCommitInterceptor(CancellationTokenSource cancel) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
            cancel.CancelAsync();
    }
}
