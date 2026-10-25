using Croft.Shared;
using Croft.Web.Options;
using Croft.Web.Services;
using Bunit;
using Microsoft.AspNetCore.Components.Infrastructure;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MudBlazor.Services;

namespace Croft.Web.Tests;

/// <summary>
/// bUnit context for component tests (Task R2.4's project setup): MudBlazor services, loose JS
/// interop, a fake <see cref="IIconCatalog"/>, the site's <see cref="Branding"/>,
/// <see cref="BlogOptions"/> and bUnit's authorization test doubles — e.g.
/// <c>Auth.SetRoles("Admin")</c> before rendering.
/// </summary>
public abstract class BlogBunitContext : BunitContext
{
    /// <summary>bUnit's test double for <see cref="Microsoft.AspNetCore.Components.PersistentComponentState"/>,
    /// used to verify [PersistentState] restoration in tests.</summary>
    protected BunitPersistentComponentState PersistentState { get; }

    protected BlogBunitContext()
    {
        // WORKAROUND(W-048, .NET): the cloned culture with ISO short dates for the MudBlazor pickers, applied in tests as in production
        // The same culture configuration both apps apply at startup (AppCulture), so component
        // tests render under exactly the production culture.
        AppCulture.ConfigureDefaults();
        // END WORKAROUND(W-048)
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton<IIconCatalog, FakeIconCatalog>();
        Services.AddSingleton<IMarkdownRenderer, MarkdownRenderer>();
        Services.AddScoped<SessionExpiryHooks>();
        Services.AddSingleton<FakeBlogApiClient>();
        Services.AddSingleton<IBlogApiClient>(sp => sp.GetRequiredService<FakeBlogApiClient>());
        // WORKAROUND(W-077, bUnit): BunitPersistentComponentState must be registered before other services are resolved
        // Add BunitPersistentComponentState early, before any services are retrieved, so it's
        // available for tests that need to verify [PersistentState] restoration.
        PersistentState = AddBunitPersistentComponentState();
        // bUnit does not register the framework's [PersistentState] value provider either, so without
        // it a [PersistentState] property is never persisted nor restored in a test (MainLayout's
        // handoff ran in no test at all: review 2026-10-19, I-13).
        Services.AddSupplyValueFromPersistentComponentStateProvider();
        // END WORKAROUND(W-077)
        // The site's identity and words, as BrandingService would build them from the site's
        // branding folder at startup. Fully qualified: "Branding" alone resolves to the
        // Croft.Web namespace member here.
        Services.AddSingleton(new Croft.Web.Branding
        {
            Name = "Test Blog",
            Tagline = "Testing, one line at a time",
            FileHashes = new Dictionary<string, string>
            {
                ["logo.svg"] = "logohash",
                ["favicon.ico"] = "faviconhash",
                ["apple-touch-icon.png"] = "touchhash",
                ["fallback-banner.svg"] = "bannerhash",
            },
            FallbackBannerFile = "fallback-banner.svg",
        });
        // Fully qualified: "Options" alone resolves to the Croft.Web.Options namespace here.
        Services.AddSingleton<IOptions<BlogOptions>>(Microsoft.Extensions.Options.Options.Create(new BlogOptions
        {
            BaseUrl = "http://localhost:5022/",
        }));
        Auth = this.AddAuthorization();
    }

    /// <summary>bUnit's authorization test double; configure with <c>SetRoles(...)</c> etc.</summary>
    protected BunitAuthorizationContext Auth { get; }
}
