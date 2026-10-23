using AwesomeAssertions;
using Microsoft.Extensions.Logging;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 11a item 2: <c>.well-known/security.txt</c> (RFC 9116) is parsed at startup and its
/// <c>Contact</c> and <c>Expires</c> lines drive one Warning log line each (missing Contact,
/// missing Expires, unparsable Expires, Expires in the past, Expires within 30 days). The site
/// must not fail startup or readiness over any of these — the Warning is the only effect. The
/// within-30-days case is logged again, once a day, while it stays so, by a hosted service
/// that ticks on <see cref="TimeProvider"/>.
/// </summary>
public class SecurityTxtCheckerTests
{
    private static SecurityTxtChecker Make(FakeTimeProvider time, ListLogger logger) =>
        new(time, new SecurityTxtCheckerLoggerAdapter(logger));

    /// <summary>The DI-registered <see cref="ILogger{T}"/> wraps a non-generic
    /// <see cref="ILogger"/>; the test fixture does the same by hand, holding the
    /// <see cref="ListLogger"/>.</summary>
    private sealed class SecurityTxtCheckerLoggerAdapter(ListLogger inner) : ILogger<SecurityTxtChecker>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => inner.Log(logLevel, eventId, state, exception, formatter);
    }

    [Fact]
    public void Check_NoFile_LogsNothing()
    {
        var logger = new ListLogger();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var checker = Make(time, logger);

        checker.Check(null);

        // No file = no warnings. The brief's rules only fire when security.txt exists.
        logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Check_EmptyFile_LogsBothMissing()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var logger = new ListLogger();
        var checker = Make(time, logger);

        checker.Check("");

        logger.Warnings.Should().ContainMatch("*Contact*",
            "the message names the missing required field");
        logger.Warnings.Should().ContainMatch("*Expires*");
    }

    [Fact]
    public void Check_NoContact_LogsMissingContact_AndDoesNotMentionExpiry()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var logger = new ListLogger();
        var checker = Make(time, logger);

        checker.Check("Expires: 2027-01-01T00:00:00Z");

        logger.Warnings.Should().ContainMatch("*Contact*");
        logger.Warnings.Should().NotContainMatch("*Expires*");
    }

    [Fact]
    public void Check_NoExpires_LogsMissingExpires_AndDoesNotMentionContact()
    {
        var logger = new ListLogger();
        var checker = Make(new FakeTimeProvider(DateTimeOffset.UnixEpoch), logger);

        checker.Check("Contact: mailto:security@example.com");

        logger.Warnings.Should().ContainMatch("*Expires*");
        logger.Warnings.Should().NotContainMatch("*Contact*");
    }

    [Fact]
    public void Check_ExpiresInThePast_LogsExpiredOn()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var logger = new ListLogger();
        var checker = Make(time, logger);

        checker.Check("Contact: mailto:security@example.com\nExpires: 2026-01-01T00:00:00Z");

        logger.Warnings.Should().ContainMatch("*expired on*2026-01-01*");
    }

    [Fact]
    public void Check_ExpiresWithin30Days_LogsExpiresSoon()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var logger = new ListLogger();
        var checker = Make(time, logger);

        // 29 days out — within the 30-day threshold.
        checker.Check("Contact: mailto:security@example.com\nExpires: 2026-01-30T00:00:00Z");

        logger.Warnings.Should().ContainMatch("*2026-01-30*");
        logger.Warnings.Should().ContainMatch("*30-day*");
    }

    [Fact]
    public void Check_ExpiresAtExactly30Days_LogsExpiresSoon()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var logger = new ListLogger();
        var checker = Make(time, logger);

        checker.Check("Contact: mailto:security@example.com\nExpires: 2026-01-31T00:00:00Z");

        logger.Warnings.Should().ContainMatch("*2026-01-31*");
        logger.Warnings.Should().ContainMatch("*30-day*");
    }

    [Fact]
    public void Check_ExpiresAt31Days_LogsNothing()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var logger = new ListLogger();
        var checker = Make(time, logger);

        checker.Check("Contact: mailto:security@example.com\nExpires: 2026-02-01T00:00:00Z");

        logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Check_UnparsableExpires_LogsUnparsable()
    {
        var logger = new ListLogger();
        var checker = Make(new FakeTimeProvider(DateTimeOffset.UnixEpoch), logger);

        checker.Check("Contact: mailto:security@example.com\nExpires: tomorrow");

        logger.Warnings.Should().ContainMatch("*parseable*");
    }

    [Fact]
    public void Check_StartOfLineAnchor_IgnoresContactInsideOtherWords()
    {
        // RFC 9116 says the field starts at the beginning of the line, with optional leading
        // whitespace. A simple value-of-line check is the contract the brief asks for: a
        // line that merely mentions the prefix mid-line does not satisfy the rule.
        var logger = new ListLogger();
        var checker = Make(new FakeTimeProvider(DateTimeOffset.UnixEpoch), logger);

        checker.Check("X-Contact: mailto:security@example.com\nExpires: 2027-01-01T00:00:00Z");

        logger.Warnings.Should().ContainMatch("*Contact*",
            "X-Contact is a different directive, not Contact — the rule must look at line starts");
    }

    [Fact]
    public void Check_NeitherContactNorExpires_LogsBoth()
    {
        var logger = new ListLogger();
        var checker = Make(new FakeTimeProvider(DateTimeOffset.UnixEpoch), logger);

        checker.Check("# this is a comment\n");

        logger.Warnings.Should().ContainMatch("*Contact*");
        logger.Warnings.Should().ContainMatch("*Expires*");
    }

    [Fact]
    public void Check_DoesNotThrow_AndDoesNotFailStartup_ForAnyCombination()
    {
        // The brief's headline rule: the site must not fail startup or readiness over a malformed
        // security.txt. The checker only logs.
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var checker = Make(time, new ListLogger());

        var act = () => checker.Check("garbage\nExpires: never\n");

        act.Should().NotThrow();
    }

    [Fact]
    public async Task DailyTick_ReschedulesUntilExpiresStaysWithin30Days()
    {
        // The brief: "Warning at startup and once a day while it stays so". Drive the clock
        // forward one day at a time; the checker must log a new warning for each day the file is
        // still within 30 days of now.
        var clock = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(clock);
        var logger = new ListLogger();
        var checker = Make(time, logger);

        checker.Check("Contact: mailto:security@example.com\nExpires: 2026-01-29T00:00:00Z");
        var startupCount = logger.Warnings.Count;

        for (var i = 0; i < 3; i++)
        {
            clock = clock.AddDays(1);
            time.UtcNow = clock;
            await checker.DailyTickAsync(CancellationToken.None);
        }

        logger.Warnings.Count.Should().Be(startupCount + 3,
            "one Warning per day, while Expires stays within 30 days");
    }

    [Fact]
    public async Task DailyTick_NothingToLog_LogsNothing()
    {
        // A site with a healthy security.txt and no work to do: no daily log spam.
        var clock = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(clock);
        var logger = new ListLogger();
        var checker = Make(time, logger);

        checker.Check("Contact: mailto:security@example.com\nExpires: 2027-01-01T00:00:00Z");
        var startupCount = logger.Warnings.Count;

        for (var i = 0; i < 5; i++)
        {
            clock = clock.AddDays(1);
            time.UtcNow = clock;
            await checker.DailyTickAsync(CancellationToken.None);
        }

        logger.Warnings.Count.Should().Be(startupCount,
            "no Warnings for a healthy security.txt, day after day");
    }

    [Fact]
    public async Task DailyTick_AfterExpiry_SwitchesFromWindowWarningToExpiredWarning_OncePerDay()
    {
        // Expires on day 28. Ticks on days 1..28 (28 days down to 0 days left) log the
        // within-30-days line, ticks on days 29 and 30 log the expired line: one line per tick,
        // none missed and none doubled, and the expired line keeps coming daily until the file
        // is replaced.
        var clock = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(clock);
        var logger = new ListLogger();
        var checker = Make(time, logger);

        checker.Check("Contact: mailto:security@example.com\nExpires: 2026-01-29T00:00:00Z");
        var startupCount = logger.Warnings.Count;

        for (var i = 0; i < 30; i++)
        {
            clock = clock.AddDays(1);
            time.UtcNow = clock;
            await checker.DailyTickAsync(CancellationToken.None);
        }

        logger.Warnings.Count.Should().Be(startupCount + 30, "one line per daily tick");
        logger.Warnings.Count(w => w.Contains("30-day")).Should().Be(1 + 28,
            "the startup line and ticks on days 1..28 are within the window");
        logger.Warnings.Count(w => w.Contains("expired on")).Should().Be(2,
            "days 29 and 30 are past the expiry, one expired line each");
    }

    [Fact]
    public async Task DailyTick_TwoConditions_EachWarnsOncePerDay()
    {
        // Missing Contact and missing Expires are both active. Each is logged once a day: the
        // startup check and a tick on the same day add nothing, the next day adds one of each.
        var clock = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(clock);
        var logger = new ListLogger();
        var checker = Make(time, logger);

        checker.Check("# nothing useful\n");
        logger.Warnings.Should().HaveCount(2);

        await checker.DailyTickAsync(CancellationToken.None);
        logger.Warnings.Should().HaveCount(2, "the same day again repeats nothing");

        time.UtcNow = clock.AddHours(23);
        await checker.DailyTickAsync(CancellationToken.None);
        logger.Warnings.Should().HaveCount(2, "still the same UTC day");

        time.UtcNow = clock.AddDays(1);
        await checker.DailyTickAsync(CancellationToken.None);
        logger.Warnings.Should().HaveCount(4, "a day later each condition warns again, once");
        logger.Warnings.Count(w => w.Contains("Contact")).Should().Be(2);
        logger.Warnings.Count(w => w.Contains("Expires")).Should().Be(2);
    }
}
