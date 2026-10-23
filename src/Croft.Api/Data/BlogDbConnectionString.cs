using System.Globalization;
using Npgsql;

namespace Croft.Api.Data;

/// <summary>
/// Builds every Croft.Api database connection string from <c>ConnectionStrings:BlogDb</c> with
/// this deployment's pool settings: <c>Database:MaxPoolSize</c> (Npgsql's Maximum Pool Size,
/// default 20) and <c>Database:MaxAutoPrepare</c> (Max Auto Prepare, default 0 = off, Npgsql's
/// own default). The DbContext and the cache listener both build here — the listener then sets
/// its own Application Name and Keep Alive on the returned builder and turns pooling off (a
/// dedicated LISTEN connection, brief 13h item 3). A present-but-invalid setting throws with the setting's name, so a typo fails at
/// startup (Program.cs builds once before serving or migrating) instead of failing every
/// request with a conversion error.
/// </summary>
public static class BlogDbConnectionString
{
    // WORKAROUND(W-054, Npgsql): Npgsql's default pool of 100 per process exhausted Postgres at about 55 req/s; capped at 20
    private const int DefaultMaxPoolSize = 20;
    private const int DefaultMaxAutoPrepare = 0;
    // END WORKAROUND(W-054)

    /// <summary>The read replica's Application Name, so tests and pg_stat_activity can tell reads from writes.</summary>
    public const string ReadApplicationName = "croft-api-read";

    public static NpgsqlConnectionStringBuilder Build(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var baseString = configuration.GetConnectionString("BlogDb")
            ?? throw new InvalidOperationException("Connection string 'BlogDb' is not configured.");

        return new NpgsqlConnectionStringBuilder(baseString)
        {
            MaxPoolSize = ReadSetting(configuration, "Database:MaxPoolSize", DefaultMaxPoolSize, minimum: 1),
            MaxAutoPrepare = ReadSetting(configuration, "Database:MaxAutoPrepare", DefaultMaxAutoPrepare, minimum: 0)
        };
    }

    /// <summary>
    /// The read connection: BlogDb with Host (and only Host) replaced by Database:ReadHost,
    /// plus the read Application Name and a read-only default transaction — a write on it
    /// fails in tests exactly as it would on a real replica. Database:ReadHost may be an
    /// Npgsql multi-host list (<c>db1,db2,db3</c>): only the Host keyword is replaced, so
    /// port, database, user and password still come from BlogDb and the replicas must share
    /// them.
    /// </summary>
    public static NpgsqlConnectionStringBuilder Read(IConfiguration configuration)
    {
        var readHost = configuration["Database:ReadHost"]
            ?? throw new InvalidOperationException("Setting 'Database:ReadHost' is not configured.");
        return new NpgsqlConnectionStringBuilder(Build(configuration).ConnectionString)
        {
            Host = readHost,
            ApplicationName = ReadApplicationName,
            Options = "-c default_transaction_read_only=on"
        };
    }

    /// <summary>
    /// The routing rule, in one place, no per-query decisions: the read connection serves
    /// public reads — GET/HEAD with no signed-in user — and only when Database:ReadHost is
    /// set. Everything else uses the primary: admin, editor, a signed-in reader, every write,
    /// and anything without an HttpContext (background services, migrations).
    /// Accepted consequence (owner, 2026-10-15: "eventual consistency is the price of
    /// distributed systems; admin/editor pages can always use the primary"): a replica can
    /// lag the primary by milliseconds to seconds, so an anonymous reader may briefly see the
    /// previous version. A cache refill must not pin that lag for the entry's five minutes, so
    /// a refill of a group invalidated within <c>Croft:Api:Cache:PrimaryAfterInvalidation</c>
    /// (default 60 s) reads the primary instead (<see cref="PrimaryRefill"/>, review 2026-10-19
    /// finding 2): staleness after an edit is bounded by that read, not by the cache expiry.
    /// </summary>
    public static string ForRequest(IConfiguration configuration, IHttpContextAccessor httpContextAccessor)
    {
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        return !string.IsNullOrWhiteSpace(configuration["Database:ReadHost"])
                && PublicReadRequest.IsAnonymousRead(httpContextAccessor.HttpContext)
            ? Read(configuration).ConnectionString
            : Build(configuration).ConnectionString;
    }

    /// <summary>An unset setting keeps its default; anything else must be an integer at or above <paramref name="minimum"/>.</summary>
    private static int ReadSetting(IConfiguration configuration, string name, int defaultValue, int minimum)
    {
        var raw = configuration[name];
        if (raw is null)
            return defaultValue;

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < minimum)
            throw new InvalidOperationException($"{name} must be an integer >= {minimum}, got '{raw}'.");

        return value;
    }
}
