using System.Text.Json;
using AwesomeAssertions;

namespace Croft.Web.Tests;

/// <summary>
/// The generated manifest.webmanifest (brief 07a item 2): name and short_name = the site's Name,
/// theme_color = light primary, background_color = light bg, display standalone, and the icons
/// from /branding/ (each with its content-hash cache-buster).
/// </summary>
public class BrandingManifestTests
{
    private static readonly Branding Known = Branding.Defaults with
    {
        Name = "Field Notes",
        Light = BrandPalette.DefaultLight.With("primary", "#9A3412").With("bg", "#FFEDD5"),
        FileHashes = new Dictionary<string, string>
        {
            ["logo.svg"] = "logohash",
            ["favicon.ico"] = "faviconhash",
            ["apple-touch-icon.png"] = "touchhash",
            ["fallback-banner.svg"] = "bannerhash",
        },
        FallbackBannerFile = "fallback-banner.svg",
    };

    private static JsonElement Manifest() =>
        JsonElement.Parse(new BrandingService(Known).ManifestJson);

    [Fact]
    public void Manifest_NamesTheSite()
    {
        var manifest = Manifest();

        manifest.GetProperty("name").GetString().Should().Be("Field Notes");
        manifest.GetProperty("short_name").GetString().Should().Be("Field Notes");
    }

    [Fact]
    public void Manifest_ColoursComeFromTheLightPalette()
    {
        var manifest = Manifest();

        manifest.GetProperty("theme_color").GetString().Should().Be("#9A3412", "theme_color is the light primary");
        manifest.GetProperty("background_color").GetString().Should().Be("#FFEDD5", "background_color is the light bg");
    }

    [Fact]
    public void Manifest_IsStandaloneAndIconsComeFromBranding()
    {
        var manifest = Manifest();

        manifest.GetProperty("display").GetString().Should().Be("standalone");
        var icons = manifest.GetProperty("icons").EnumerateArray().ToList();
        icons.Should().Contain(i => i.GetProperty("src").GetString() == "/branding/logo.svg?v=logohash"
            && i.GetProperty("type").GetString() == "image/svg+xml");
        icons.Should().Contain(i => i.GetProperty("src").GetString() == "/branding/apple-touch-icon.png?v=touchhash"
            && i.GetProperty("sizes").GetString() == "180x180");
        icons.Should().OnlyContain(i => i.GetProperty("src").GetString()!.StartsWith("/branding/", StringComparison.Ordinal),
            "every icon is served from the branding endpoint");
    }
}
