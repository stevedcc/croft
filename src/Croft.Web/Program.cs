using System.Buffers;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Croft.Shared;
using Croft.Shared.Contracts;
using Croft.Shared.Telemetry;
using Croft.Shared.Warmup;
using Croft.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Croft.Web.Authentication;
using Croft.Web.Caching;
using Croft.Web.Components;
using Croft.Web.Data;
using Croft.Web.Options;
using Croft.Web.Security;
using Croft.Web.Security.Csp;
using Croft.Web.Services;
using Croft.Web.Theme;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MudBlazor;
using MudBlazor.Services;

AppCulture.ConfigureDefaults();

var builder = WebApplication.CreateBuilder(args);

// Kestrel names itself in a Server header by default; nothing is gained by that, so it is off.
// Kestrel's minimum data rates and connection ceiling (RequestLimits) bound slow readers and senders.
builder.WebHost.ConfigureKestrel((context, server) =>
{
    server.AddServerHeader = false;
    RequestLimits.ConfigureKestrel(server, context.Configuration, "Web");
});

// Metrics, traces and logs go to the owner's collector over OTLP — but only when
// OTEL_EXPORTER_OTLP_ENDPOINT is set; without it nothing is registered and the app behaves
// exactly as it did before. Probes are never traced and exported data is privacy-scrubbed.
builder.AddCroftTelemetry(CroftApp.Web);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        options.DetailedErrors = builder.Environment.IsDevelopment();
        // WORKAROUND(W-052, Blazor): Blazor's defaults (100 circuits, 3 minutes) OOM-killed the pods under reload churn; CircuitSettings holds the measured values
        // Every reload abandons its old circuit and starts a new one, so the disconnected pool is
        // pure cost beside the live circuits. Blazor's defaults (100 per server for 3 minutes)
        // OOM-killed the pods under reload churn; Croft's modest defaults are CircuitSettings.
        var circuits = CircuitSettings.Read(builder.Configuration);
        options.DisconnectedCircuitMaxRetained = circuits.DisconnectedMaxRetained;
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromSeconds(circuits.DisconnectedRetentionSeconds);
        // END WORKAROUND(W-052)
    })
    .AddHubOptions(options =>
    {
        // WORKAROUND(W-010, Blazor): the 32 KiB SignalR receive limit aborts the circuit on one editor input event past about 16,301 characters
        // One MarkdownEditor input event carries the textarea value TWICE (the field value and
        // the ChangeEventArgs value), JSON-escaped UTF-8 — measured: 2 net hub-message bytes per
        // ASCII character, 6 for all-`€` text (3 UTF-8 bytes × 2 occurrences). At the
        // 200,000-character content limit (Croft.Shared.PostContent) the worst case is
        // 200,000 × 6 = 1,200,000 bytes; × 1.25 headroom = 1,500,000; the smallest power of two
        // that carries it is 2^21 = 2,097,152 bytes. The 32 KiB default aborts the circuit on
        // one input event past ~16,301 ASCII characters and loses the text.
        options.MaximumReceiveMessageSize = 2_097_152;
        // END WORKAROUND(W-010)
    });

builder.Services.AddHttpContextAccessor();

// An anonymous reader's page comes from this pod's memory (brief 14b): the settings are read once
// here; ReadingPageCachePolicy decides what is cached and ReadingPageCache puts it in the pipeline.
var outputCacheSettings = OutputCacheSettings.Read(builder.Configuration);
builder.Services.AddSingleton(outputCacheSettings);
builder.Services.AddOutputCache(options =>
{
    options.SizeLimit = outputCacheSettings.SizeLimitMegabytes * 1024L * 1024L;
    options.MaximumBodySize = outputCacheSettings.MaximumBodyKilobytes * 1024L;
    options.AddPolicy(ReadingPageCachePolicy.PolicyName, new ReadingPageCachePolicy(outputCacheSettings));
    options.AddPolicy(FeedCachePolicy.PolicyName, new FeedCachePolicy(outputCacheSettings));
    options.AddPolicy(MediaCachePolicy.PolicyName, new MediaCachePolicy(outputCacheSettings));
});
// The documents the cache may store are compressed inside the cache (brief 14c item 5).
builder.Services.AddReadingPageCompression();
// Only the ingress decides a client's address (brief 13b, review finding 8): X-Forwarded-For and
// -Proto are honoured from the peers in ForwardedHeaders:KnownNetworks (CIDR list; the k8s base sets the
// cluster's pod network), one hop (ForwardLimit 1: the entry the proxy appended is the client).
// Empty means no proxy is trusted and the headers are ignored (local dev). The pipeline runs the
// middleware once, below; ASPNETCORE_FORWARDEDHEADERS_ENABLED is not set anywhere, because it
// clears the trusted networks and so trusts every peer.
var forwardedNetworks = builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [];
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    // The defaults trust loopback; only the configured networks are trusted here.
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();
    foreach (var network in forwardedNetworks)
    {
        options.KnownIPNetworks.Add(System.Net.IPNetwork.TryParse(network, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"ForwardedHeaders:KnownNetworks entry '{network}' is not a CIDR network such as 10.42.0.0/16."));
    }
});

var oidcAuthority = builder.Configuration["Oidc:Authority"];
var oidcClientId = builder.Configuration["Oidc:ClientId"];
var useOidc = !string.IsNullOrWhiteSpace(oidcAuthority) && !string.IsNullOrWhiteSpace(oidcClientId);

// TLS ends at the ingress, so "same as request" gave the cookies no Secure flag: outside
// Development every cookie the site sets is Secure. (Development serves plain http.)
var cookieSecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;

builder.Services.AddAntiforgery(options => options.Cookie.SecurePolicy = cookieSecurePolicy);

// WORKAROUND(W-092, Blazor): a static reading page must get no antiforgery token, so no cookie and no no-store headers
var antiforgery = builder.Services.Last(descriptor => descriptor.ServiceType == typeof(IAntiforgery));
builder.Services.Remove(antiforgery);
builder.Services.AddSingleton<IAntiforgery>(provider => new StaticDocumentAntiforgery(
    (IAntiforgery)ActivatorUtilities.CreateInstance(provider, antiforgery.ImplementationType!)));
// END WORKAROUND(W-092)

var authBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = useOidc
        ? OpenIdConnectDefaults.AuthenticationScheme
        : CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
{
    options.Cookie.SecurePolicy = cookieSecurePolicy;
    options.Events = new CookieAuthenticationEvents
    {
        OnValidatePrincipal = TokenSessionEvents.OnValidatePrincipal
    };
});

if (useOidc)
{
    // Oidc:Scopes is an indexed list (Oidc__Scopes__0, Oidc__Scopes__1, ...). A gap (only
    // index 1 set) or a blank entry does not bind as written and would silently fall back to the
    // default list, so it stops the start, like the other required settings below.
    var scopesSection = builder.Configuration.GetSection("Oidc:Scopes");
    var scopeEntries = scopesSection.GetChildren().ToList();
    var configuredScopes = scopesSection.Get<string[]>();
    if (scopeEntries.Count > 0
        && (configuredScopes is null
            || Enumerable.Range(0, scopeEntries.Count).Any(i =>
                scopesSection[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] is null)
            || configuredScopes.Length != scopeEntries.Count
            || configuredScopes.Any(string.IsNullOrWhiteSpace)))
    {
        throw new InvalidOperationException(
            "Oidc:Scopes must be a list without gaps or blank entries (Oidc__Scopes__0, Oidc__Scopes__1, ...); "
            + $"found the indices {string.Join(", ", scopeEntries.Select(child => child.Key))}.");
    }

    authBuilder.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
    {
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.GetClaimsFromUserInfoEndpoint = true;
        options.Authority = oidcAuthority;
        options.ClientId = oidcClientId;
        options.ClientSecret = builder.Configuration["Oidc:ClientSecret"];
        options.ResponseType = "code";
        // Oidc:Scopes replaces the list (a provider that rejects "groups" says so with
        // invalid_scope); "openid" is always asked for, and the default list is the original.
        options.Scope.Clear();
        options.Scope.Add("openid");
        foreach (var scope in configuredScopes is { Length: > 0 }
            ? configuredScopes
            : ["profile", "email", "groups", "offline_access"])
        {
            options.Scope.Add(scope.Trim());
        }
        options.CallbackPath = "/signin-oidc";
        options.SignedOutCallbackPath = "/signout-callback-oidc";
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            NameClaimType = "name",
            RoleClaimType = ClaimTypes.Role
        };
        // Tokens live in the server-side ITokenStore keyed by the blog_sid claim,
        // never in the cookie.
        options.SaveTokens = false;
        if (!builder.Environment.IsDevelopment())
        {
            // The sign-in redirect's cookies are SameSite=None; the framework's own default
            // already makes them Secure on plain http, which Development relies on.
            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
            options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
        }
        options.Events = new OpenIdConnectEvents
        {
            OnTokenValidated = TokenSessionEvents.OnTokenValidated,
            OnRedirectToIdentityProviderForSignOut = TokenSessionEvents.OnRedirectToIdentityProviderForSignOut
        };
    });

    // Back-channel logout validates logout tokens against the same OIDC configuration the
    // login uses: the handler's configuration manager (same metadata, same signing keys).
    builder.Services.AddSingleton(provider =>
    {
        var options = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);
        return new BackchannelLogoutTokenValidator(
            oidcClientId!,
            options.ConfigurationManager!,
            provider.GetRequiredService<TimeProvider>());
    });
    builder.Services.AddSingleton<BackchannelLogoutService>();
}

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

// The Web database (ConnectionStrings:WebDb) is optional: it holds the state several Web
// instances must share (token sessions, Data Protection keys). It is registered only when the
// connection string is set — without it the app runs database-less, as the single-instance
// default must be able to, and no CroftWebDbContext exists to reach for. With it, login sessions
// are shared rows (any Web instance can serve, refresh and end any signed-in user's session) and
// the expired ones are purged by a background job; without it the single-instance default keeps
// the in-memory store.
var webDbConnectionString = builder.Configuration.GetConnectionString("WebDb");
if (!string.IsNullOrWhiteSpace(webDbConnectionString))
{
    builder.Services.AddDbContextFactory<CroftWebDbContext>(options => options.UseNpgsql(webDbConnectionString));
    builder.Services.AddSingleton<PostgresTokenStore>();
    builder.Services.AddSingleton<ITokenStore>(services => services.GetRequiredService<PostgresTokenStore>());
    builder.Services.AddSingleton<ISessionLock>(services => services.GetRequiredService<PostgresTokenStore>());
    builder.Services.AddHostedService<SessionPurgeHostedService>();
}
else
{
    builder.Services.AddSingleton<InMemoryTokenStore>();
    builder.Services.AddSingleton<ITokenStore>(services => services.GetRequiredService<InMemoryTokenStore>());
    builder.Services.AddSingleton<ISessionLock>(services => services.GetRequiredService<InMemoryTokenStore>());
    builder.Services.AddHostedService<InMemorySessionPurgeHostedService>();
}
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<IUserAccessTokenService, UserAccessTokenService>();
builder.Services.AddBlogApiClient(builder.Configuration);
builder.Services.AddExceptionHandler<ApiUnavailableExceptionHandler>();
builder.Services.AddScoped<SessionExpiryHooks>();
builder.Services.AddScoped<IBlogApiClient, BlogApiClient>();
builder.Services.AddSingleton<IMarkdownRenderer, MarkdownRenderer>();
builder.Services.AddSingleton<IIconCatalog, IconCatalog>();
builder.Services.AddSingleton<ISitemapService, SitemapService>();
builder.Services.AddSingleton<StaticAssetIntegrity>();
builder.Services.AddSingleton<IFeedService, FeedService>();
// Brief 14a: the pod warms itself up (its own public reads, over loopback) before it reports ready.
builder.Services.AddCroftWarmup<WebWarmupPlan>(builder.Configuration);

builder.Services.Configure<BlogOptions>(builder.Configuration.GetSection(BlogOptions.SectionName));
builder.Services.Configure<RoleGroupSettings>(builder.Configuration.GetSection(RoleGroupSettings.SectionName));
builder.Services.Configure<CspOptions>(builder.Configuration.GetSection(CspOptions.SectionName));

// The CSP contributors that may add sources per request. Scripts are allowed by directory
// (absolute, with the site's origin) per kind of document, plus the hash of the theme bootstrap
// script in App.razor's head, which must run before first paint and so stays inline (its text
// and the hash share one source: ThemeBootstrapScript); see ScriptSourcesCspContributor.
// Analytics adds nothing: the pageview beacon posts to
// /api/analytics/pageview on this same origin, so connect-src 'self' already covers it and
// no contributor is registered for it.
builder.Services.AddSingleton<IDocumentCspContributor, ScriptSourcesCspContributor>();
builder.Services.AddSingleton<IDocumentCspContributor, TrustedTypesCspContributor>();

// The staff pages under /admin and /editor are the only documents allowed inline style
// attributes (MudBlazor's grids, dialogs and tabs write them); every other document keeps
// style-src-attr 'none' because the public markup writes none.
builder.Services.AddSingleton<ICspContributor, AdminAreaCspContributor>();

// MudThemeProvider's inline style blocks (the theme variables of both modes and the chart
// block MudBlazor renders beside them) are allowed the same way, by their hashes computed at
// startup — see MudThemeStyleCspContributor for why both modes go on every response and what
// a runtime MudTheme change would need. The scrollbar block is not hashed: it is off
// (MainLayout's DefaultScrollbar) and app.css carries its rules verbatim.
var mudThemeStyles = new MudThemeStyleCspContributor();
// The served theme.css and its cache-buster (filled at startup, below).
var themeStylesheet = new ThemeStylesheet();
builder.Services.AddSingleton(themeStylesheet);
builder.Services.AddSingleton<IDocumentCspContributor>(mudThemeStyles);

builder.Services.AddSingleton(ArrayPool<byte>.Shared);
builder.Services.AddSingleton<CspReportRateLimiter>();
builder.Services.AddSingleton<CspReportEndpoint>();

// Overload shedding: at most Croft:Web:MaxConcurrentRequests page requests in progress and
// Croft:Web:RequestQueueLimit waiting (per client Croft:Web:MaxConcurrentRequestsPerClient, media
// Croft:Web:MaxConcurrentMediaRequests, see RequestLimits); beyond that the reader gets a fast 503 with Retry-After
// instead of queueing until croft-web runs out of memory. The exempt paths are cheap or
// must-answer: health probes, SignalR circuits (long-lived connections, not requests), the
// framework's assets, the static files MapStaticAssets serves, and /csp-report (its own limiter).
// The limits are read when the one shared limiter is created (the first request), so they come
// from the host's full configuration however it was supplied.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status503ServiceUnavailable;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "5";
        await context.HttpContext.Response.WriteAsync("Too many requests", cancellationToken);
    };

    // The global limiter chained with a per-client one (the forwarded address; see RequestLimits)
    // and a separate partition for /api/media/, so a slow image holds neither the pages' permits
    // nor, beyond its own ceiling, anyone else's.
    options.GlobalLimiter = RequestLimits.CreateLimiter(
        builder.Configuration,
        "Web",
        defaultPerClient: 16,
        defaultMediaPerClient: 16,
        exempt: IsRateLimiterExempt,
        isMedia: context => context.Request.Path.StartsWithSegments("/api/media") || context.Request.Path.StartsWithSegments("/social/avatar"),
        clientKey: context => RequestLimits.ClientKey(context.Connection.RemoteIpAddress) ?? "unknown");
});

// Circuit admission (the request limiter above exempts /_blazor: circuits are connections, not
// requests). The process's circuit count — a disconnected circuit still counts until Blazor
// closes it — is published as croft.web.circuits.active and refuses NEW circuit starts at
// Croft:Web:Circuits:MaxActive (0 = no limit); reconnects of existing circuits go through at
// any count. The reader keeps the prerendered content and loses only interactivity.
builder.Services.AddSingleton<CircuitCounter>();
builder.Services.AddScoped<CircuitHandler, CircuitCountingHandler>();
builder.Services.AddSingleton(_ => CircuitSettings.Read(builder.Configuration));
builder.Services.AddSingleton(_ => AuthSettings.Read(builder.Configuration));
// The cookie lasts as long as the session behind it (Auth:SessionLifetime, sliding): the session store is the
// real limit, so a shorter cookie would sign a member of staff out of a session that is still valid.
builder.Services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
    .Configure<AuthSettings>((options, auth) => options.ExpireTimeSpan = auth.SessionLifetime);

// The site's identity is read from the branding folder once at startup and never changes:
// one immutable Branding for every reader (and for the generated theme.css/manifest below).
builder.Services.AddSingleton(sp => BrandingService.Load(
    sp.GetRequiredService<IOptions<BlogOptions>>().Value.BrandingPath,
    Path.Combine(sp.GetRequiredService<IWebHostEnvironment>().WebRootPath, "branding-default"),
    sp.GetRequiredService<ILogger<BrandingService>>()));
builder.Services.AddSingleton(sp => sp.GetRequiredService<BrandingService>().Current);
builder.Services.AddSingleton(sp => BlogTheme.Create(sp.GetRequiredService<Branding>()));

// The site's well-known files (security.txt, WebFinger, Apple's AASA and similar) are read
// from Blog:SiteFilesPath once at startup. A missing or empty folder means no site files;
// a malformed file fails startup naming the file. The endpoint serves them as exact-path,
// GET/HEAD-only, with a 1-hour Cache-Control and a strong content-hash ETag.
builder.Services.AddSingleton(sp => SiteFilesService.Load(
    sp.GetRequiredService<IOptions<BlogOptions>>().Value.SiteFilesPath));

// The security.txt check (brief 11a item 2): runs at startup against the file's bytes (already
// in the SiteFiles snapshot) and re-emits the within-30-days Warning once a day from the
// hosted service below. The check is a singleton because the brief asks for "Warning at startup
// and once a day while it stays so" — the cached body is the steady state.
builder.Services.AddSingleton<SecurityTxtChecker>();
// Registered unconditionally, like the checker: the database-less single-instance default gets
// the daily warning too.
builder.Services.AddHostedService<SecurityTxtHostedService>();

// Data Protection keys must be readable by every Web instance and survive a restart: with the
// Web database they are rows in it (every instance reads the same key ring, so a cookie one
// instance protected is readable by all), and with only DataProtection:KeysPath (the blog-web-keys
// PVC mounted at /keys in k8s) they are files there, exactly as before. Without either, local dev
// keeps the default behaviour. Both stores carry the same application name, so keys imported from
// a file ring ("keys import") protect the auth cookies exactly as before — the switch signs
// nobody out.
var keysPath = builder.Configuration["DataProtection:KeysPath"];
IDataProtectionBuilder? dataProtection = null;
if (!string.IsNullOrWhiteSpace(webDbConnectionString))
{
    // WORKAROUND(W-028, ASP.NET Core): key rings are isolated by application name, so the file and database rings share one to keep imported keys valid
    dataProtection = builder.Services.AddDataProtection()
        .PersistKeysToDbContext<CroftWebDbContext>()
        .SetApplicationName("blog-web");
    // END WORKAROUND(W-028)
}
else if (!string.IsNullOrWhiteSpace(keysPath))
{
    // WORKAROUND(W-028, ASP.NET Core): key rings are isolated by application name, so the file and database rings share one to keep imported keys valid
    dataProtection = builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
        .SetApplicationName("blog-web");
    // END WORKAROUND(W-028)
}

// Optional: encrypt the key ring with an RSA certificate (the key ring and the sessions it
// protects live in the same database, so without one a copy of the database yields both).
var keyRingEncrypted = KeyRingEncryption.Apply(
    dataProtection ?? builder.Services.AddDataProtection(), builder.Configuration, out var keyRingUnencryptedReason);

builder.Services.AddMudServices();

var app = builder.Build();

// One Warning per serving process (the migrate init container uses no key ring).
if (!keyRingEncrypted && dataProtection is not null && args is not ["migrate"])
{
    KeyRingEncryption.WarnUnencrypted(app.Logger, keyRingUnencryptedReason);
}

// The k8s init container's command ("dotnet Croft.Web.dll migrate"): apply the pending Web
// migrations and exit. MigrationRunner serialises concurrent runs (two pods starting at once)
// on an advisory lock, so both complete and the schema is migrated exactly once. Without
// ConnectionStrings:WebDb there is no database to migrate (the database-less single-instance
// default), so the entry is a successful no-op instead of a start-up failure.
if (args is ["migrate"])
{
    // Retries a database that is away (a restart), then exits with code 1 instead of throwing:
    // an unhandled exception at PID 1 was seen to leave the container running for a day.
    // A fresh scope (and so a fresh connection) per attempt.
    Environment.ExitCode = await MigrationRetry.RunEntryAsync(
        builder.Configuration, app.Logger, TimeProvider.System, async () =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<CroftWebDbContext>() is { } webDb)
            {
                await MigrationRunner.RunAsync(webDb);
            }
        });
    return;
}

// The cutover job's command ("dotnet Croft.Web.dll keys import <directory>"): copy the old file
// key ring's keys into the Web database once, so switching key storage signs nobody out. Unlike
// migrate this fails when ConnectionStrings:WebDb is absent — there is no key ring to import
// into, and a silent success would end the cutover believing the keys had moved.
if (args is ["keys", ..])
{
    Environment.ExitCode = await KeysImportCommand.RunAsync(app.Services, args, Console.Out, Console.Error);
    return;
}

// A malformed branding folder must fail Web at startup, not on the first page render, so the
// singleton is built here rather than when a component first resolves it.
app.Services.GetRequiredService<BrandingService>();

// The site-files folder is also read once at startup; a disallowed file, an oversized file or
// a shadowing path is a deployment mistake and must fail here, not on the first request.
app.Services.GetRequiredService<SiteFiles>();

// Brief 11a item 2: parse .well-known/security.txt (RFC 9116) at startup and log Warning lines
// for the required-field, expiry-window and parseability conditions. Never a startup failure
// (the brief's headline rule: the site must not go down over an expiring text file).
var securityTxtChecker = app.Services.GetRequiredService<SecurityTxtChecker>();
var siteFiles = app.Services.GetRequiredService<SiteFiles>();
securityTxtChecker.Check(siteFiles.ByPath.GetValueOrDefault("/.well-known/security.txt")?.Bytes is { } bytes
    ? System.Text.Encoding.UTF8.GetString(bytes)
    : null);

// The circuit count's gauge publishes from startup (reading 0 while idle), not from the first
// circuit or request that would otherwise resolve the counter.
app.Services.GetRequiredService<CircuitCounter>();

// The theme style hashes cover the theme the app serves, so the provider is rendered here —
// at startup, with the app's MudTheme — and not on any request.
await mudThemeStyles.InitializeAsync(app.Services.GetRequiredService<MudTheme>());

themeStylesheet.Initialize(app.Services.GetRequiredService<BrandingService>().ThemeCss);

// The Web app forwards the visitor's IP to the API behind Analytics:IngestKey; without it the
// API ignores the header and per-visitor counting silently collapses to the Web pod's IP.
// Refuse to start without it outside Development. Checked after
// Build() so every configuration source is merged.
if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(builder.Configuration["Analytics:IngestKey"]))
{
    throw new InvalidOperationException("Analytics:IngestKey must be configured outside Development.");
}

// Canonical URLs, og:image and JSON-LD are all built from Blog:BaseUrl; a blank value would
// silently advertise relative or broken URLs to crawlers. start-local.sh sets it for every
// local instance, so refusing to start is a deployment error, not a dev annoyance.
if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(builder.Configuration["Blog:BaseUrl"]))
{
    throw new InvalidOperationException("Blog:BaseUrl must be configured outside Development.");
}

// An invalid Auth:SessionLifetime fails startup here, not at the first sign-in.
AuthSettings.Read(builder.Configuration);

// First in the pipeline, once: a second pass would consume the next X-Forwarded-For entry, which
// the client wrote itself (ForwardedHeadersOnceTests; after this pass the peer is the client, which
// is in no trusted network, so a stray second pass would not forward anything either). With no
// trusted network the headers are ignored and the middleware is not added at all (an empty list
// of trusted proxies would otherwise mean "trust every peer" to ForwardedHeadersMiddleware).
if (forwardedNetworks.Length > 0)
{
    app.UseForwardedHeaders();
}

// The plain security headers go out on every response, the ones the exception page answers with
// too: UseExceptionHandler clears the response (headers included) before it writes, so
// SecurityHeadersMiddleware applies them in OnStarting. CspMiddleware writes the CSP at the
// start, and in its own OnStarting it writes it again only when the document stage changed the
// header (CspMiddleware, the ChangesTheStartHeader check): a response whose document stage adds
// nothing keeps the start header, and one that removed the header itself (the site files) keeps
// it removed. Today every response the exception handler clears is re-executed as the /Error
// document, whose stage does change the header, which is what puts the error page's policy back
// on the re-executed response (ErrorResponseHeadersTests pins that).
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<CspMiddleware>();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(new ExceptionHandlerOptions
    {
        ExceptionHandlingPath = "/Error",
        CreateScopeForErrors = true,
        // An API that is down or too slow is 503 (try again), not a failure of this app.
        StatusCodeSelector = exception => ApiUnavailableException.IsUnavailable(exception)
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status500InternalServerError,
        // ApiUnavailableExceptionHandler logs that outage as a Warning; the middleware's own
        // unhandled-exception Error line would be a second, louder record of the same event.
        SuppressDiagnosticsCallback = context => ApiUnavailableException.IsUnavailable(context.Exception),
    });
}

// WORKAROUND(W-101, ASP.NET Core): endpoints match GET only, so HEAD runs the pipeline as the GET it stands for
app.UseMiddleware<HeadRequestMiddleware>();
// END WORKAROUND(W-101)

app.MapStaticAssets();

// A site file at a static asset's URL would never be served (the asset wins): refuse it now.
SiteFilesService.EnsureNoStaticAssetShadowing(
    app.Services.GetRequiredService<SiteFiles>(),
    ((IEndpointRouteBuilder)app).DataSources
        .SelectMany(source => source.Endpoints)
        .OfType<RouteEndpoint>()
        .Where(endpoint => endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.StaticAssets.StaticAssetDescriptor>() is not null)
        .Select(endpoint => "/" + endpoint.RoutePattern.RawText?.TrimStart('/')));
app.UseRouting();

// After UseRouting so the exemption can see the matched endpoint (the static-asset one).
app.UseRateLimiter();

app.UseAuthentication();

// Beside the request limiter, for the connection side: CircuitAdmissionMiddleware refuses
// circuit starts from requests that are not signed in, and circuit starts at
// Croft:Web:Circuits:MaxActive. After authentication: it needs to know who is asking.
app.UseMiddleware<CircuitAdmissionMiddleware>();

app.UseAuthorization();

app.UseAntiforgery();

// After authentication, so a signed-in request is known to be one; before the endpoints.
app.UseReadingPageCache(outputCacheSettings);

app.MapGet("/api/media/{id:guid}", async (
    Guid id,
    IHttpClientFactory httpClientFactory,
    IUserAccessTokenService tokenService,
    IConfiguration configuration,
    HttpContext context,
    CancellationToken ct) =>
{
    var token = await tokenService.GetAccessTokenAsync(context.User, ct);
    await ApiImageProxy.ForwardAsync(context, httpClientFactory, configuration, $"/api/media/{id}", id.ToString(), token, ct);
})
    .WithMetadata(MediaDocumentMetadata.Instance, CrossOriginResource.Instance)
    .CacheOutput(MediaCachePolicy.PolicyName);

// The stored 48 x 48 avatars of the discussion's replies (brief 12b): the same proxy, anonymous. Only a
// SHA-256 as 64 lower-case hex characters is asked for (the API checks it again); anything else is a 404
// without a call. Kept in the pod like an image: the API's Cache-Control (a year, immutable) is public.
app.MapGet("/social/avatar/{hash}.webp", async (
    string hash,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    HttpContext context,
    CancellationToken ct) =>
{
    if (!SocialAvatarHash.IsValid(hash))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await ApiImageProxy.ForwardAsync(context, httpClientFactory, configuration, $"/api/social/avatars/{hash}.webp", hash, token: null, ct);
})
    .WithMetadata(MediaDocumentMetadata.Instance)
    .CacheOutput(MediaCachePolicy.PolicyName);

const int MaxBeaconBytes = 4096;

app.MapPost("/api/analytics/pageview", async (
    HttpContext context,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    CancellationToken ct) =>
{
    // Analytics must never break pages: record nothing here, just forward the beacon and
    // answer 204 whatever the API says.
    if (context.User.Identity?.IsAuthenticated == true &&
        (context.User.IsInRole("Admin") || context.User.IsInRole("Editor")))
    {
        // Staff previewing their own posts must not inflate the counts.
        return Results.NoContent();
    }

    // The beacon is a page id and a referrer: 4 KiB is generous. Kestrel is told (it refuses a
    // declared or chunked body past the limit with 413 as the body is read), and the body is
    // read here under the same bound, so the limit holds on any server.
    if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
    {
        limit.MaxRequestBodySize = MaxBeaconBytes;
    }

    byte[] body;
    try
    {
        body = await ReadAtMostAsync(context.Request.Body, MaxBeaconBytes, ct);
    }
    catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    if (body.Length > MaxBeaconBytes)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    PageViewRequest? beacon = null;
    try
    {
        // WORKAROUND(W-044, Browsers): navigator.sendBeacon posts JSON as text/plain, so the body is parsed directly
        // sendBeacon sends the JSON payload as text/plain, so parse the body directly
        // instead of going through content-type-checked request JSON extensions.
        beacon = JsonSerializer.Deserialize<PageViewRequest>(body, JsonSerializerOptions.Web);
        // END WORKAROUND(W-044)
    }
    catch (JsonException)
    {
        // Malformed beacon: nothing to record.
    }

    if (beacon is not null)
    {
        try
        {
            var client = httpClientFactory.CreateClient("BlogApi");
            using var forward = new HttpRequestMessage(HttpMethod.Post, "/api/analytics/pageview")
            {
                Content = JsonContent.Create(beacon)
            };

            ApiImageProxy.ForwardVisitor(forward, context, configuration);

            var userAgent = context.Request.Headers.UserAgent.ToString();
            if (!string.IsNullOrEmpty(userAgent))
            {
                forward.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            }

            await client.SendAsync(forward, ct);
        }
        catch (Exception)
        {
            // Analytics must never break pages.
        }
    }

    return Results.NoContent();
});

// Browsers post policy violations to the path the CSP's report-uri and report-to name. The
// endpoint lives outside the Blazor router, stays anonymous (it is a receiver, not a surface)
// and does its own format, size and rate checks; see CspReportEndpoint for the semantics.
app.MapPost("/csp-report", (CspReportEndpoint endpoint, HttpContext context, CancellationToken ct) =>
    endpoint.HandleAsync(context, ct));

// Crawlers read robots.txt from the Web host: everything is crawlable except the staff, auth
// and API surfaces, and the sitemap is advertised here. Media is the exception — images must be
// fetchable for image search and link previews, so its Allow sits above Disallow: /api/ (Google
// takes the longest match, first-match crawlers take the Allow). The Sitemap URL comes from
// Blog:BaseUrl like every absolute URL; trimmed, because local dev configures the value with a
// trailing slash.
app.MapGet("/robots.txt", (IOptions<BlogOptions> blogOptions) =>
{
    var baseUrl = blogOptions.Value.BaseUrl.TrimEnd('/');

    return Results.Text($"""
        User-agent: *
        Allow: /
        Disallow: /admin
        Disallow: /editor
        Disallow: /login
        Disallow: /logout
        Disallow: /signin-oidc
        Disallow: /signout-callback-oidc
        Allow: /api/media/
        Disallow: /api/
        Sitemap: {baseUrl}/sitemap.xml

        """, "text/plain; charset=utf-8");
}).WithMetadata(CrossOriginResource.Instance);

// Crawlers read sitemap.xml from the Web host: the home page, every published post (lastmod is
// its UpdatedAt) and every category and tag with at least one published post — no drafts, no
// scheduled posts, no admin pages. The URLs come from Blog:BaseUrl like every absolute URL, and
// the document is the same published-only list for every caller.
app.MapGet("/sitemap.xml", async (HttpContext context, ISitemapService sitemap, OutputCacheSettings cacheSettings, CancellationToken ct) =>
    {
        // Kept by the output cache for FeedDuration (brief 14b); browsers and proxies may keep it as long.
        context.Response.Headers.CacheControl = FeedCachePolicy.CacheControlValue(cacheSettings);
        return Results.Text(await sitemap.BuildXmlAsync(ct), "application/xml; charset=utf-8");
    })
    .WithMetadata(FeedDocumentMetadata.Instance, CrossOriginResource.Instance)
    .CacheOutput(FeedCachePolicy.PolicyName);

// Readers and crawlers pull the Atom feed from the Web host: the 20 newest published posts,
// newest first, described exactly like the post page's head describes them — no drafts, no
// scheduled posts. The URLs come from Blog:BaseUrl like every absolute URL, and the document
// is the same published-only feed for every caller.
app.MapGet("/feed.xml", async (HttpContext context, IFeedService feed, OutputCacheSettings cacheSettings, CancellationToken ct) =>
    {
        context.Response.Headers.CacheControl = FeedCachePolicy.CacheControlValue(cacheSettings);
        return Results.Text(await feed.BuildXmlAsync(ct), "application/atom+xml; charset=utf-8");
    })
    .WithMetadata(FeedDocumentMetadata.Instance, CrossOriginResource.Instance)
    .CacheOutput(FeedCachePolicy.PolicyName);

// The site's colours and web app manifest are generated once at startup from the branding
// folder (BrandingService). theme.css is linked with its cache-buster (?v= is the hash of the
// exact served text, ThemeStylesheet.Version) and cached forever; the manifest changes with the
// site's name and colours and is never cached hard.
app.MapGet("/branding/theme.css", (HttpContext context, ThemeStylesheet theme) =>
{
    context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    return Results.Text(theme.Css, "text/css; charset=utf-8");
});

app.MapGet("/manifest.webmanifest", (HttpContext context, BrandingService branding) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Text(branding.ManifestJson, "application/manifest+json; charset=utf-8");
}).WithMetadata(CrossOriginResource.Instance);

// The five branding images: the site's file if present, else the framework default from
// wwwroot/branding-default. Only these names are served (anything else, including encoded
// traversal, is 404) and every link to them carries the file's content hash (?v=), so they
// are cached forever.
app.MapGet("/branding/{file}", (HttpContext context, BrandingService branding, string file) =>
{
    if (branding.ResolveFile(file) is not { } path)
    {
        return Results.NotFound();
    }

    context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
    return Results.File(path, BrandingContentType(path));
}).WithMetadata(CrossOriginResource.Instance);

static string BrandingContentType(string path) => Path.GetExtension(path) switch
{
    ".svg" => "image/svg+xml",
    ".ico" => "image/x-icon",
    ".png" => "image/png",
    ".jpg" => "image/jpeg",
    _ => "application/octet-stream",
};

// Site files: security.txt, WebFinger, Apple's AASA and similar. The folder is read once at
// startup and each known file gets its own exact-path route — registered here, before the
// Blazor router, so a site file never goes through the catch-all (a missing site file at any
// path returns Blazor's 404, not the site's). The route takes every method so that anything
// but GET and HEAD answers 405 with `Allow: GET, HEAD` from here: were the route GET/HEAD-only,
// a POST would fall through to the Blazor catch-all, where UseAntiforgery makes it a 400.
// A strong content-hash ETag (If-None-Match → 304);
// Cache-Control: public, max-age=3600. No cookies, no CSP document header (these are not
// documents: only .txt, .json and a couple of well-known names, see SiteFilesService). The
// CSP middleware always writes a header, so the endpoint clears it before the response
// starts. Symlinks (a Kubernetes ConfigMap's `..data/` entries) work because the service
// reads the bytes once at startup and never re-reads.

// WebFinger (brief 11a item 3) gets its own endpoint — the response is the file's bytes only
// when the ?resource query matches the file's subject or one of its aliases (the acct: scheme
// and host compared case-insensitively, the local part exactly), with the JRD content type and
// an open CORS header (Mastodon fetches it cross-origin). The file is optional: a site that
// publishes no webfinger simply has no endpoint, but a malformed one fails startup here,
// naming the file. Everything else is the same as the site-file loop below (strong content
// ETag, one-hour Cache-Control, nosniff, no cookies, no CSP document header); the loop skips
// this file, since it would serve the bytes to any query unconditionally. The site file's
// name is always exactly <c>.well-known/webfinger</c> — SiteFilesService's allowed-name rule
// (item 1) is exact and fails startup on any case variant — so the endpoint maps at that
// literal RFC 7033 path and the loop skips the file by the same name comparison, not by
// object identity (review finding #1).
var webfingerFile = app.Services.GetRequiredService<SiteFiles>().ByPath.Values
    .FirstOrDefault(file => string.Equals(file.RelativePath, WebFingerDocument.RelativePath, StringComparison.Ordinal));
if (webfingerFile is not null)
{
    var webfinger = WebFingerDocument.Parse(webfingerFile.RelativePath, webfingerFile.Bytes);
    app.Map(WebFingerDocument.UrlPath, (HttpContext context, string? resource) =>
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            return SiteFileEndpoint.MethodNotAllowed(context);
        }

        context.Response.OnStarting(() =>
        {
            // Validators and caching belong to the answers that carry (or stand for) the file.
            if (context.Response.StatusCode is StatusCodes.Status200OK or StatusCodes.Status304NotModified)
            {
                context.Response.Headers.ETag = webfingerFile.ETag;
                context.Response.Headers.CacheControl = "public, max-age=3600";
            }

            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Access-Control-Allow-Origin"] = "*";
            context.Response.Headers.Remove("Content-Security-Policy");
            context.Response.Headers.Remove("Content-Security-Policy-Report-Only");
            return Task.CompletedTask;
        });

        if (resource is null)
        {
            return Results.BadRequest();
        }

        if (!webfinger.Matches(resource))
        {
            return Results.NotFound();
        }

        var ifNoneMatch = context.Request.Headers.IfNoneMatch;
        if (ifNoneMatch is { Count: > 0 }
            && ifNoneMatch.ToString().Contains(webfingerFile.ETag, StringComparison.Ordinal))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return Results.File(webfingerFile.Bytes, webfingerFile.ContentType);
    }).WithMetadata(new SiteFileEndpoint(), CrossOriginResource.Instance);
}

foreach (var file in app.Services.GetRequiredService<SiteFiles>().ByPath.Values)
{
    // Skip the webfinger file: the dedicated endpoint above already serves it with the
    // resource-matching rules, the JRD content type and Access-Control-Allow-Origin: *. The
    // comparison is by name — the same exact predicate the lookup above used (review finding
    // #1: not by object identity).
    if (string.Equals(file.RelativePath, WebFingerDocument.RelativePath, StringComparison.Ordinal))
    {
        continue;
    }

    app.Map(file.UrlPath, (HttpContext context) =>
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            return SiteFileEndpoint.MethodNotAllowed(context);
        }

        context.Response.OnStarting(() =>
        {
            context.Response.Headers.ETag = file.ETag;
            context.Response.Headers.CacheControl = "public, max-age=3600";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            // Site files are not documents: they are well-known small files, and a CSP on them
            // would be meaningless (they never load scripts, they never render). The middleware
            // already wrote one, so clear it before the response goes out.
            context.Response.Headers.Remove("Content-Security-Policy");
            context.Response.Headers.Remove("Content-Security-Policy-Report-Only");
            return Task.CompletedTask;
        });

        var ifNoneMatch = context.Request.Headers.IfNoneMatch;
        if (ifNoneMatch is { Count: > 0 }
            && ifNoneMatch.ToString().Contains(file.ETag, StringComparison.Ordinal))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return Results.File(file.Bytes, file.ContentType);
    })
    // The rate limiter's exempt list keys on this metadata (IsRateLimiterExempt): site files
    // are cached, cheap and hot, so they never count against the page concurrency limit.
    .WithMetadata(new SiteFileEndpoint(), CrossOriginResource.Instance);
}

// The k8s probes' endpoints (k8s/web-deployment.yaml) and any other monitor's. Anonymous by
// design: kubelet and the load balancer are not signed-in users, and these answers carry no
// content and no user state — an auth challenge here would fail every probe. Documents say
// noindex with the RobotsNoIndex meta tag; these plain-text answers say it as the X-Robots-Tag
// header, the mechanism for non-document responses, and are never cached: each answer is a
// point-in-time signal.
app.MapGet("/health/live", (HttpContext context) =>
{
    // The process serving requests IS the liveness signal. Nothing else is checked here —
    // especially not the API or the database: a restart cannot fix a dependency, so an outage
    // must surface as unready (no traffic), never as a restart loop.
    HealthHeaders(context);
    // The release workflow's smoke test starts each image with no API or database and reads the
    // version it was built as from here.
    context.Response.Headers["X-Croft-Version"] = AppVersion.Current;
    return Results.Text("Healthy", "text/plain; charset=utf-8");
});

app.MapGet("/health/ready", async (HttpContext context, IHttpClientFactory httpClientFactory, WarmupGate warmup, CancellationToken ct) =>
{
    // The warm-up gate comes first (brief 14a): a pod still warming up takes no readers, and
    // nothing else is asked until it is done. The gate opens when the warm-up finishes, fails
    // or runs out of time, so it never holds a pod back for good.
    if (!warmup.IsOpen)
    {
        HealthHeaders(context);
        return Results.Text("Warming up", "text/plain; charset=utf-8", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    // Ready = the state this pod must serve from answers: the Web database when one is
    // configured (sessions and key rings live there) and the API whose content this app serves
    // (its own /health). Each check is bounded, so the answer arrives inside the probe's
    // timeout whatever is hanging.
    var ready = true;

    if (context.RequestServices.GetService<CroftWebDbContext>() is { } webDb)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            ready = await webDb.Database.CanConnectAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is DbException or OperationCanceledException or TimeoutException)
        {
            // A database that errors or does not answer in time is not ready.
            ready = false;
        }
    }

    if (ready)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var response = await httpClientFactory.CreateClient("BlogApi").GetAsync("/health", timeout.Token);
            ready = response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TimeoutException
            || ApiUnavailableException.IsResilienceFailure(ex))
        {
            // An API that cannot be reached (or whose resilience policy gave up: timeout, open breaker) fails readiness exactly like one that answers unwell.
            ready = false;
        }
    }

    HealthHeaders(context);
    return Results.Text(ready ? "Healthy" : "Unhealthy", "text/plain; charset=utf-8",
        statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});

static void HealthHeaders(HttpContext context)
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Robots-Tag"] = "noindex";
}

if (useOidc)
{
    app.MapGet("/login", async (string? returnUrl, HttpContext context) =>
    {
        await context.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties
        {
            RedirectUri = LocalUrl.IsLocal(returnUrl) ? returnUrl! : "/"
        });
    });

    // Sign-out starts at the interstitial: GET /logout redirects to the small page whose
    // same-origin script clears every local draft backup (a shared computer keeps no drafts
    // after sign-out) and submits its form. The cookie and IdP sign-out run at POST
    // /logout/complete only, behind an antiforgery token: a link on another site (the auth
    // cookie is SameSite=Lax, so a GET would carry it) cannot sign a staff member out.
    app.MapGet("/logout", () => Results.Redirect(SignOutInterstitial.Path));
    app.MapGet(SignOutInterstitial.Path, (HttpContext context, IAntiforgery antiforgery) =>
    {
        var html = SignOutInterstitial.Render(antiforgery.GetAndStoreTokens(context));
        context.Response.Headers.CacheControl = "no-store";
        return Results.Content(html, "text/html; charset=utf-8");
    });
    app.MapPost(SignOutInterstitial.CompletePath, async (HttpContext context, ITokenStore tokenStore, IAntiforgery antiforgery) =>
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest();
        }

        // WORKAROUND(W-096, Chromium): Chromium checks form-action on the redirect hop of a form POST, so the 302 to the IdP is turned into a 200 page that continues by meta refresh
        // The store entry is removed and the cookie cleared first; LogoutAsync then writes a
        // redirect (the IdP's end-session URL, or "/" without a stored ID token). That 302 is
        // turned into a 200 page, because Chromium would block the redirect hop of a form POST
        // under form-action 'self' (W-096).
        await TokenSessionEvents.LogoutAsync(context, tokenStore);
        var next = context.Response.Headers.Location.ToString();
        context.Response.Headers.Remove("Location");
        context.Response.Headers.CacheControl = "no-store";
        context.Response.StatusCode = StatusCodes.Status200OK;
        return Results.Content(SignOutInterstitial.RenderContinue(string.IsNullOrEmpty(next) ? "/" : next), "text/html; charset=utf-8");
        // END WORKAROUND(W-096)
    });
    // A GET to the completion path (a cross-site link, an old bookmark) changes nothing: 405.
    app.MapMethods(SignOutInterstitial.CompletePath, ["GET", "HEAD"], (HttpContext context) =>
    {
        context.Response.Headers.Allow = "POST";
        return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
    });

    // WORKAROUND(W-045, Blazor): the Blazor catch-all would demand an antiforgery token for the IdP form POST, so the endpoint is mapped outside the router
    // OIDC Back-Channel Logout 1.0 (spec section 2.6): the IdP posts a signed logout_token as a
    // form field to this anonymous endpoint. It must stay outside the Blazor router (its
    // catch-all page would demand an antiforgery token) and never log the token — it is a
    // credential. The answer is 200 with Cache-Control: no-store, or 400 for an invalid token.
    app.MapPost("/backchannel-logout", async (
        HttpContext context,
        BackchannelLogoutService backchannelLogout,
        CancellationToken ct) =>
    {
        // A form the reader cannot parse (malformed body, missing boundary) is an invalid
        // logout request and must be answered with 400, not surface as a server error.
        string? logoutToken = null;
        if (context.Request.HasFormContentType)
        {
            try
            {
                logoutToken = (await context.Request.ReadFormAsync(ct))["logout_token"].ToString();
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
            }
        }
        if (!await backchannelLogout.LogoutAsync(logoutToken, ct))
        {
            return Results.BadRequest();
        }

        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok();
    });
    // END WORKAROUND(W-045)
}

// WORKAROUND(W-026, Blazor): Blazor writes its own frame-ancestors header; disabled so the CSP middleware is the only writer
// Blazor writes its own frame-ancestors CSP on rendered responses; ours carries
// frame-ancestors 'none' and must be the only CSP header, so the framework's is disabled.
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode(options => options.ContentSecurityFrameAncestorsPolicy = null)
    .CacheOutput(ReadingPageCachePolicy.PolicyName);
// END WORKAROUND(W-026)

app.Run();

// Reads the body up to limit + 1 bytes: a result longer than limit means the body is over it.
static async Task<byte[]> ReadAtMostAsync(Stream body, int limit, CancellationToken ct)
{
    var buffer = new byte[limit + 1];
    var total = 0;
    while (total < buffer.Length)
    {
        var read = await body.ReadAsync(buffer.AsMemory(total), ct);
        if (read == 0)
            break;
        total += read;
    }

    return buffer[..total];
}

// Static files are identified by the endpoint that serves them (MapStaticAssets), not by a file
// extension: theme.css, robots.txt and the feed are generated endpoints and must not slip
// through the exemption.
static bool IsRateLimiterExempt(HttpContext context)
{
    var path = context.Request.Path;
    return path.StartsWithSegments("/health")
        || path.StartsWithSegments("/_blazor")
        || path.StartsWithSegments("/_framework")
        || path.Equals("/csp-report")
        // Site files (security.txt, WebFinger, Apple's AASA and similar): cached, cheap, hot.
        || context.GetEndpoint()?.Metadata.GetMetadata<SiteFileEndpoint>() is not null
        // WORKAROUND(W-027, ASP.NET Core): MapStaticAssets files are recognisable only by their StaticAssetDescriptor endpoint metadata, not by extension
        || context.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.StaticAssets.StaticAssetDescriptor>() is not null;
        // END WORKAROUND(W-027)
}
