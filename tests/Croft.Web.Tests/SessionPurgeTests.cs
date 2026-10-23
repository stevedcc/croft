using System.Data.Common;
using Croft.Web.Data;
using Croft.Web.Options;
using Croft.Web.Services;
using Croft.Web.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Croft.Web.Tests;

/// <summary>
/// Two Web instances' purge ticks reaching the expired-session purge at the same time: the job's
/// advisory lock must let exactly one of them do the work, so the purge happens once per tick
/// across the deployment and the instances do not fight over the same rows. The purgers are two
/// <see cref="SessionPurgeHostedService"/> instances over one database (their app hosts are not
/// started here: each host's background loop would run unattended purge ticks of its own and
/// pollute the counts). The winner is held mid-work until the loser has had its turn, so the two
/// ticks genuinely overlap instead of racing on task scheduling.
/// </summary>
[Trait("Category", "Container")]
public class SessionPurgeTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExpiredSession_IsPurgedOnce_WithTwoPurgersRunning()
    {
        await SeedAsync(
            expired: new TokenEntry("at-dead", "rt-dead", Now.AddHours(-1), Sub: "admin-id", SessionExpiresAt: Now.AddHours(-1)),
            live: new TokenEntry("at-live", "rt-live", Now.AddHours(1), Sub: "admin-id", SessionExpiresAt: Now.AddHours(1)));
        var workGate = new HoldFirstDeleteInterceptor();
        await using var dbA = CreateDbContext(workGate);
        await using var dbB = CreateDbContext();
        var purgerA = NewPurger();
        var purgerB = NewPurger();

        var purgeA = purgerA.PurgeAsync(dbA);
        // A's purge is mid-work (its delete waits at the gate) and holds the job's advisory lock.
        await workGate.WorkStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var resultB = await purgerB.PurgeAsync(dbB).WaitAsync(TimeSpan.FromSeconds(30));
        workGate.Release();
        var resultA = await purgeA.WaitAsync(TimeSpan.FromSeconds(30));

        new[] { resultA, resultB }.Count(result => !result.Skipped).Should().Be(1,
            "the advisory lock lets exactly one instance's purge tick do the work");
        resultA.Should().Be(new SessionPurgeResult(Skipped: false, Purged: 1),
            "the instance holding the lock purges the expired session");
        resultB.Should().Be(new SessionPurgeResult(Skipped: true, Purged: 0),
            "the other instance's tick finds the lock taken and skips instead of purging a second time");
        var expiredRow = await FindAsync("expired-1");
        expiredRow.Should().BeNull("the expired session is dead weight and must be gone");
        var liveRow = await FindAsync("live-1");
        liveRow.Should().NotBeNull("a session that has not expired must survive the purge");
    }

    private static SessionPurgeHostedService NewPurger()
    {
        // The scope factory serves only the hosted service's own loop (unused by PurgeAsync).
        using var services = new ServiceCollection().BuildServiceProvider();
        return new SessionPurgeHostedService(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SessionPurgeHostedService>.Instance,
            new FakeTimeProvider(Now),
            new AuthSettings(TimeSpan.FromDays(30)));
    }

    private async Task SeedAsync(TokenEntry expired, TokenEntry live)
    {
        await using var db = CreateDbContext();
        db.UserSessions.Add(ToSession("expired-1", expired));
        db.UserSessions.Add(ToSession("live-1", live));
        await db.SaveChangesAsync();
    }

    private static UserSession ToSession(string sessionId, TokenEntry entry) => new()
    {
        SessionId = sessionId,
        Sid = entry.Sid,
        Sub = entry.Sub,
        AccessToken = entry.AccessToken,
        RefreshToken = entry.RefreshToken,
        IdToken = entry.IdToken,
        ExpiresAt = entry.ExpiresAt,
        SessionExpiresAt = entry.SessionExpiresAt
    };

    private async Task<UserSession?> FindAsync(string sessionId)
    {
        await using var db = CreateDbContext();
        return await db.UserSessions.FindAsync(sessionId);
    }

    private CroftWebDbContext CreateDbContext(DbCommandInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<CroftWebDbContext>().UseNpgsql(fixture.ConnectionString);
        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }
        return new CroftWebDbContext(options.Options);
    }

    /// <summary>
    /// Test-only gate: holds the first DELETE on the sessions table until it is released, so the
    /// purger running it can be observed mid-work — holding the advisory lock — before the second
    /// purger starts.
    /// </summary>
    private sealed class HoldFirstDeleteInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _deletes;

        public TaskCompletionSource WorkStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("DELETE FROM \"UserSessions\"", StringComparison.Ordinal)
                && Interlocked.Increment(ref _deletes) == 1)
            {
                WorkStarted.TrySetResult();
                // Bounded so a purge that never reaches its delete surfaces as a failed test,
                // not a hung run.
                await _release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            return result;
        }
    }
}
