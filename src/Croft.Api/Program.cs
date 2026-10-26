using System.Net;
using System.Security.Claims;
using Croft.Api;
using Croft.Api.Data;
using Croft.Api.Errors;
using Croft.Api.Identity;
using Croft.Api.Security;
using Croft.Api.Services;
using Croft.Api.Services.ContentExport;
using Croft.Api.Social;
using Croft.Shared;
using Croft.Shared.Telemetry;
using Croft.Shared.Warmup;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

AppCulture.ConfigureDefaults();

var builder = WebApplication.CreateBuilder(args);

// Kestrel names itself in a Server header by default; nothing is gained by that, so it is off.
// Kestrel's minimum data rates and connection ceiling (RequestLimits) bound slow readers and senders.
builder.WebHost.ConfigureKestrel((context, server) =>
{
    server.AddServerHeader = false;
    RequestLimits.ConfigureKestrel(server, context.Configuration, "Api");
});

// Metrics, traces and logs go to the owner's collector over OTLP — but only when
// OTEL_EXPORTER_OTLP_ENDPOINT is set; without it nothing is registered and the app behaves
// exactly as it did before. Probes are never traced and exported data is privacy-scrubbed.
builder.AddCroftTelemetry(CroftApp.Api);

builder.Services.AddOpenApi();
builder.Services.AddControllers();
// Readiness asks the primary database; liveness (below) asks nothing.
// The warm-up gate (brief 14a) is one more readiness condition: Unhealthy while the pod warms up.
builder.Services.AddHealthChecks()
    .AddCheck<WarmupHealthCheck>("warmup", tags: ["ready"])
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);
builder.Services.AddCroftWarmup<ApiWarmupPlan>(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

builder.Services.AddDbContext<BlogDbContext>(
    (serviceProvider, options) => BlogDbOptions.Configure(serviceProvider, options),
    optionsLifetime: ServiceLifetime.Scoped);
builder.Services.AddSingleton<PrimaryRefillConnectionInterceptor>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.Authority = builder.Configuration["Oidc:Authority"];
        options.MapInboundClaims = false;

        var audience = builder.Configuration["Oidc:Audience"];
        var isDevelopment = builder.Environment.IsDevelopment();

        options.TokenValidationParameters = new TokenValidationParameters
        {
            NameClaimType = "name",
            RoleClaimType = ClaimTypes.Role,
            ValidateAudience = !isDevelopment,
            ValidAudience = audience
        };

        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                // The API identifies users by sub (authorship, ownership): a token without one is
                // refused here, so no endpoint ever sees such a principal.
                if (string.IsNullOrEmpty(context.Principal?.FindFirstValue("sub")))
                {
                    context.HttpContext.RequestServices.GetRequiredService<MissingSubLog>()
                        .Refused(context.Principal?.FindFirstValue("iss"));
                    context.Fail("The token has no sub claim.");
                    return Task.CompletedTask;
                }

                if (context.Principal?.Identity is ClaimsIdentity identity)
                {
                    var settings = context.HttpContext.RequestServices
                        .GetRequiredService<IOptions<RoleGroupSettings>>().Value;
                    RoleGroupMapper.AddRoleClaims(identity, settings);
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddSingleton<MissingSubLog>();
builder.Services.Configure<RoleGroupSettings>(
    builder.Configuration.GetSection(RoleGroupSettings.SectionName));
builder.Services.AddAuthorization();

builder.Services.AddScoped<ISlugService, SlugService>();
builder.Services.AddScoped<IPostService, PostService>();
builder.Services.AddScoped<ITagService, TagService>();
builder.Services.AddScoped<ICategoryService, CategoryService>();
builder.Services.AddScoped<ISearchService, SearchService>();
builder.Services.AddScoped<ISitemapQueryService, SitemapQueryService>();
builder.Services.AddScoped<IAnalyticsService, AnalyticsService>();
builder.Services.AddScoped<IFeedbackService, FeedbackService>();

builder.Services.AddSingleton<IFileStorage>(
    _ => new LocalFileStorage(builder.Configuration.GetValue("FileStorage:BasePath", "uploads")!));
// The public read cache is bounded: at most 2000 entries (Size 1 each), so a flood of distinct
// anonymous reads cannot grow memory without limit. The 5-minute expiry stays the safety net
// for invalidation notifications that got lost.
builder.Services.AddMemoryCache(options => options.SizeLimit = 2000);
// After an invalidation the next refill of that group reads the primary for this long (a replica
// may not have replayed the write yet); outside the window refills use the replica as before.
builder.Services.AddSingleton<IPublicReadCache>(sp => new MemoryPublicReadCache(
    sp.GetRequiredService<IMemoryCache>(),
    sp.GetRequiredService<TimeProvider>(),
    builder.Configuration.GetValue("Croft:Api:Cache:PrimaryAfterInvalidation", MemoryPublicReadCache.DefaultPrimaryAfterInvalidation)));
builder.Services.AddSingleton<ICacheNotifier, CacheNotifier>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<DateTimeNormalizer>();
builder.Services.AddSingleton<IScheduledPublishWakeup, ScheduledPublishWakeup>();
builder.Services.AddScoped<IMediaService, MediaService>();

// Social replies (brief 12a): the settings, the link validation behind a post's two URLs, and the clients that talk to
// Mastodon, Bluesky and the avatar hosts. Off unless Social:Enabled is set.
builder.Services.Configure<SocialOptions>(builder.Configuration.GetSection(SocialOptions.SectionName));
// The site's address (brief 12d) is Blog:BaseUrl, outside the Social section: announcing needs it for the post's address.
builder.Services.AddOptions<SocialOptions>().Configure<IConfiguration>((options, configuration) => options.BaseUrl = configuration["Blog:BaseUrl"]?.Trim() ?? string.Empty);
builder.Services.AddSocialClients(builder.Configuration);
builder.Services.AddSingleton<IBlueskyHandleResolver, BlueskyHandleResolver>();
builder.Services.AddScoped<ISocialLinkService, SocialLinkService>();
builder.Services.AddScoped<IAnnouncementOutbox, AnnouncementOutbox>();
builder.Services.AddScoped<IAnnouncementService, AnnouncementService>();
builder.Services.AddScoped<IDiscussionService, DiscussionService>();
builder.Services.AddScoped<ISocialCountsReader, SocialCountsReader>();
builder.Services.AddSingleton<SocialPlatformGates>();
builder.Services.AddSingleton<ISocialAnnouncer, MastodonAnnouncer>();
builder.Services.AddSingleton<ISocialAnnouncer, BlueskyAnnouncer>();
builder.Services.AddSingleton<ISocialThreadSource, MastodonThreadSource>();
builder.Services.AddSingleton<ISocialThreadSource, BlueskyThreadSource>();
builder.Services.AddSingleton<ISocialAvatars, SocialAvatarService>();
builder.Services.AddHostedService<SocialFetchHostedService>();
builder.Services.AddHostedService<SocialAnnounceHostedService>();

builder.Services.AddHostedService<ScheduledPublisherHostedService>();
builder.Services.AddHostedService<PostSummaryBackfillService>();
builder.Services.AddHostedService<PageViewRetentionHostedService>();
builder.Services.AddSingleton<CacheListenerService>();
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<CacheListenerService>());

// Overload shedding, the same shape as croft-web's: at most Croft:Api:MaxConcurrentRequests
// requests in progress and Croft:Api:RequestQueueLimit waiting; beyond that a fast 503 with
// Retry-After instead of queueing. Health probes are exempt so an overloaded pod's liveness
// stays observable. The limits are read when the one shared limiter is created (the first
// request), so they come from the host's full configuration however it was supplied.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status503ServiceUnavailable;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "5";
        await context.HttpContext.Response.WriteAsync("Too many requests", cancellationToken);
    };

    // The global limiter chained with a per-client one and a separate partition for /api/media
    // (see RequestLimits). The API's caller is the Web pod, so a client is the visitor address
    // Web forwards in X-Visitor-IP, honoured only beside the analytics ingest key (the pairing the
    // page-view beacon already uses). A request with no trusted visitor address (Web's own page
    // reads) has no client to count against: the Web pod is not a client, and its pages are
    // limited per visitor there.
    options.GlobalLimiter = RequestLimits.CreateLimiter(
        builder.Configuration,
        "Api",
        defaultPerClient: 32,
        defaultMediaPerClient: 32,
        exempt: IsRateLimiterExempt,
        isMedia: context => context.Request.Path.StartsWithSegments("/api/media"),
        clientKey: context => TrustedVisitorKey(context, builder.Configuration["Analytics:IngestKey"]));
});

var app = builder.Build();

// Pool settings (Database:MaxPoolSize, Database:MaxAutoPrepare) are validated once here so a
// typo stops the pod at boot naming the setting — for every entry: serving, and the k8s init
// container's migrate command below. Without this the same error would surface per request
// (or inside the cache listener's reconnect loop) instead.
_ = BlogDbConnectionString.Build(builder.Configuration);

// Read routing, logged once at startup at Information: which host public reads route to
// (never the password), or that they use the primary when Database:ReadHost is unset.
var readHost = builder.Configuration["Database:ReadHost"];
if (string.IsNullOrWhiteSpace(readHost))
    StartupLogging.LogReadsOnPrimary(app.Logger);
else
    StartupLogging.LogReadsRouted(app.Logger, readHost);

// The k8s init container's command ("dotnet Croft.Api.dll migrate"): apply the pending
// migrations and exit. MigrationRunner serialises concurrent runs (two pods starting at once)
// on an advisory lock, so both complete and the schema is migrated exactly once.
var command = StartupCommand.Parse(args);

if (command.Kind == StartupCommandKind.Migrate)
{
    // Retries a database that is away (a restart), then exits with code 1 instead of throwing:
    // an unhandled exception at PID 1 was seen to leave the container running for a day.
    // A fresh scope (and so a fresh connection) per attempt.
    Environment.ExitCode = await MigrationRetry.RunEntryAsync(
        builder.Configuration, app.Logger, TimeProvider.System, async () =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            await MigrationRunner.RunAsync(scope.ServiceProvider.GetRequiredService<BlogDbContext>());
        });
    return;
}

// "dotnet Croft.Api.dll export --out <dir>": save the content as JSON plus Markdown files (a
// read-only transaction, so it also runs against a read replica) and exit; exit code 1 on any
// failure, like migrate. "import --from <dir>" restores such files into an empty database.
if (command.Kind == StartupCommandKind.Export)
{
    Environment.ExitCode = await ContentCommands.ExportAsync(
        app.Services, builder.Configuration, app.Logger, TimeProvider.System, command.Directory!);
    return;
}

if (command.Kind == StartupCommandKind.Import)
{
    Environment.ExitCode = await ContentCommands.ImportAsync(
        app.Services, builder.Configuration, app.Logger, TimeProvider.System, command.Directory!);
    return;
}

// A mistyped command (a missing option, a typo in a CronJob's args) must not fall through and
// start the web server inside a Job that would then never finish.
if (command.Kind == StartupCommandKind.Invalid)
{
    StartupLogging.LogContentCommandUsage(app.Logger);
    Environment.ExitCode = 1;
    return;
}

// Analytics hashes visitor IPs with Analytics:HashKey and forwards them only with
// Analytics:IngestKey; refuse to start without them outside Development.
// Development may run without them (dev-only values live in .env). Checked after Build()
// so every configuration source is merged.
if (!builder.Environment.IsDevelopment())
{
    if (string.IsNullOrWhiteSpace(builder.Configuration["Analytics:HashKey"]))
    {
        throw new InvalidOperationException("Analytics:HashKey must be configured outside Development.");
    }

    if (string.IsNullOrWhiteSpace(builder.Configuration["Analytics:IngestKey"]))
    {
        throw new InvalidOperationException("Analytics:IngestKey must be configured outside Development.");
    }

    // Audience validation runs outside Development; with an empty audience every token is
    // rejected at request time instead, which is far harder to notice than a startup failure.
    // The value must equal the OIDC client ID.
    if (string.IsNullOrWhiteSpace(builder.Configuration["Oidc:Audience"]))
    {
        throw new InvalidOperationException(
            "Oidc:Audience must be configured outside Development (it must equal the OIDC client ID).");
    }
}

// The plain security headers and the API's CSP go out on every response, the ones the error handler
// answers with too: the middleware applies them in OnStarting, because the handler clears the response first.
app.UseMiddleware<SecurityHeadersMiddleware>();

app.UseExceptionHandler();

// The shed is about the API's own load, so it runs before authentication touches a request.
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
// /health/live is the process alone (no check runs); /health/ready and its alias /health, kept for
// older Web pods and monitors, run the database check: 503 while the primary cannot be reached.
var readyOptions = new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") };
// /health/live also names the version the image was built as (X-Croft-Version, without the +commit
// build metadata): the release workflow's smoke test starts each image with no database and reads it.
var croftVersion = ContentCommands.CroftVersion().Split('+')[0];
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    // The default writer's output (status as plain text), plus the version header.
    ResponseWriter = (context, report) =>
    {
        context.Response.Headers["X-Croft-Version"] = croftVersion;
        context.Response.ContentType = "text/plain";
        return context.Response.WriteAsync(report.Status.ToString());
    },
});
app.MapHealthChecks("/health/ready", readyOptions);
app.MapHealthChecks("/health", readyOptions);

app.Run();

static bool IsRateLimiterExempt(HttpContext context) =>
    context.Request.Path.StartsWithSegments("/health");

static string? TrustedVisitorKey(HttpContext context, string? ingestKey) =>
    AnalyticsIngestKey.Matches(context.Request.Headers["X-Analytics-Key"].FirstOrDefault(), ingestKey)
    && IPAddress.TryParse(context.Request.Headers["X-Visitor-IP"].FirstOrDefault(), out var visitor)
        ? RequestLimits.ClientKey(visitor)
        : null;

static partial class StartupLogging
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Public reads use the primary connection: Database:ReadHost is not set.")]
    public static partial void LogReadsOnPrimary(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unknown command line. Usage: (no arguments) | migrate | export --out <dir> | import --from <dir>")]
    public static partial void LogContentCommandUsage(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Public reads route to read host {ReadHost}.")]
    public static partial void LogReadsRouted(ILogger logger, string readHost);
}
