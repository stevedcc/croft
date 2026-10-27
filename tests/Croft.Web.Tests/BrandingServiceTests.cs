using System.Security.Cryptography;
using AwesomeAssertions;

namespace Croft.Web.Tests;

/// <summary>
/// BrandingService.Load: merging branding.json over the built-in defaults (field by field and
/// token by token), rejecting malformed input with a message naming the field, one Information
/// line when there is nothing to merge, one Warning per below-AA contrast pair, and a content
/// hash per served image file (site file if present, else the framework default).
/// </summary>
public class BrandingServiceTests : IDisposable
{
    private readonly string _siteFolder = NewFolder();
    private readonly string _defaultsFolder = NewFolder();
    private readonly ListLogger _log = new();

    public void Dispose()
    {
        Directory.Delete(_siteFolder, recursive: true);
        Directory.Delete(_defaultsFolder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string NewFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blog-branding-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private BrandingService Load() =>
        BrandingService.Load(_siteFolder, _defaultsFolder, _log);

    private void WriteJson(string json) =>
        File.WriteAllText(Path.Combine(_siteFolder, "branding.json"), json);

    private static void WriteFile(string folder, string name, string content) =>
        File.WriteAllText(Path.Combine(folder, name), content);

    private static string Sha256Prefix(string content) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)))[..16].ToLowerInvariant();

    [Fact]
    public void Load_MissingFolder_LogsOneInformationLineAndUsesDefaults()
    {
        var service = BrandingService.Load(
            Path.Combine(Path.GetTempPath(), $"blog-branding-missing-{Guid.NewGuid():N}"), _defaultsFolder, _log);

        service.Current.Name.Should().Be("My Blog", "the built-in name is the default");
        service.Current.Tagline.Should().BeEmpty();
        service.Current.Description.Should().BeEmpty();
        service.Current.Author.Should().BeEmpty();
        service.Current.Light.Tokens.Should().BeEquivalentTo(BrandPalette.DefaultLight.Tokens,
            "an absent palette keeps every default token");
        service.Current.Dark.Tokens.Should().BeEquivalentTo(BrandPalette.DefaultDark.Tokens);
        _log.Information.Should().ContainSingle("a missing folder is one Information line, not noise");
    }

    [Fact]
    public void Load_MissingBrandingJson_LogsOneInformationLineAndUsesDefaults()
    {
        // The folder exists but carries no branding.json: same as a missing folder.
        var service = Load();

        service.Current.Name.Should().Be("My Blog");
        _log.Information.Should().ContainSingle();
    }

    [Fact]
    public void Load_EmptyBrandingPath_UsesDefaultsWithOneInformationLine()
    {
        // BLOG_BRANDING_PATH= (empty) means defaults: no folder is read at all.
        var service = BrandingService.Load("", _defaultsFolder, _log);

        service.Current.Name.Should().Be("My Blog");
        _log.Information.Should().ContainSingle();
    }

    [Fact]
    public void Load_PartialJson_OverridesOnlyTheGivenFields()
    {
        WriteJson("""{ "name": "Field Notes" }""");

        var branding = Load().Current;

        branding.Name.Should().Be("Field Notes");
        branding.Tagline.Should().BeEmpty("the default tagline stays when the field is absent");
        branding.Description.Should().BeEmpty();
        branding.Author.Should().BeEmpty();
    }

    [Theory]
    [InlineData("tagline", "An example site for the blog framework")]
    [InlineData("description", "Notes from the field.")]
    [InlineData("author", "Example Author")]
    public void Load_EachTextField_MergesIndependently(string field, string value)
    {
        WriteJson($$"""{ "{{field}}": "{{value}}" }""");

        var branding = Load().Current;

        branding.Name.Should().Be("My Blog", "the other fields keep their defaults");
        branding.GetType().GetProperty(char.ToUpperInvariant(field[0]) + field[1..])!
            .GetValue(branding).Should().Be(value);
    }

    [Fact]
    public void Load_PartialPalette_OverridesOnlyTheGivenTokens()
    {
        WriteJson("""{ "palette": { "light": { "primary": "#9A3412" } } }""");

        var branding = Load().Current;

        branding.Light["primary"].Should().Be("#9A3412");
        branding.Light["bg"].Should().Be(BrandPalette.DefaultLight["bg"], "untouched light tokens keep their defaults");
        branding.Dark["primary"].Should().Be(BrandPalette.DefaultDark["primary"], "the other mode is untouched");
    }

    [Theory]
    [MemberData(nameof(AllTokens))]
    public void Load_EveryToken_MergesPerToken(string token)
    {
        WriteJson($$"""{ "palette": { "light": { "{{token}}": "#123456" }, "dark": { "{{token}}": "#654321" } } }""");

        var branding = Load().Current;

        branding.Light[token].Should().Be("#123456");
        branding.Dark[token].Should().Be("#654321");
    }

    public static TheoryData<string> AllTokens()
    {
        var data = new TheoryData<string>();
        foreach (var token in PaletteTokens.All)
        {
            data.Add(token);
        }

        return data;
    }

    [Fact]
    public void Load_InvalidColor_NamesTheFieldAndTheValue()
    {
        WriteJson("""{ "palette": { "dark": { "primary": "red" } } }""");

        var act = () => Load();

        act.Should().Throw<BrandingException>()
            .WithMessage("*palette.dark.primary*\"red\"*is not a colour*");
    }

    [Theory]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("123456")]
    [InlineData("rgb(300,0,0)")]
    [InlineData("hsl(0,0%,0%)")]
    public void Load_EachRejectedColorShape_NamesItsField(string value)
    {
        WriteJson($$"""{ "palette": { "light": { "muted": "{{value}}" } } }""");

        var act = () => Load();

        act.Should().Throw<BrandingException>()
            .WithMessage("*palette.light.muted*");
    }

    [Theory]
    [InlineData("#ABC")]
    [InlineData("#a1b2c3")]
    [InlineData("#A1B2C3D4")]
    [InlineData("rgb(1,2,3)")]
    [InlineData("rgb(1, 2, 3)")]
    [InlineData("rgba(1,2,3,.38)")]
    [InlineData("rgba(1, 2, 3, 0.5)")]
    public void Load_EachValidColorShape_IsAccepted(string value)
    {
        WriteJson($$"""{ "palette": { "light": { "code-bg": "{{value}}" } } }""");

        Load().Current.Light["code-bg"].Should().Be(value);
    }

    [Fact]
    public void Load_UnknownToken_NamesTheField()
    {
        WriteJson("""{ "palette": { "light": { "primaryt": "#123456" } } }""");

        var act = () => Load();

        act.Should().Throw<BrandingException>()
            .WithMessage("*palette.light.primaryt*");
    }

    [Fact]
    public void Load_MalformedJson_NamesTheFile()
    {
        WriteJson("{ not json");

        var act = () => Load();

        act.Should().Throw<BrandingException>().WithMessage("*branding.json*");
    }

    [Fact]
    public void Load_WrongFieldType_NamesTheField()
    {
        WriteJson("""{ "name": 42 }""");

        var act = () => Load();

        act.Should().Throw<BrandingException>().WithMessage("*name*");
    }

    [Fact]
    public void Load_UnknownTopLevelField_NamesTheField()
    {
        WriteJson("""{ "nam": "Field Notes" }""");

        var act = () => Load();

        act.Should().Throw<BrandingException>().WithMessage("*nam*");
    }

    [Fact]
    public void Load_PaletteNotAnObject_NamesTheField()
    {
        WriteJson("""{ "palette": 5 }""");

        var act = () => Load();

        act.Should().Throw<BrandingException>().WithMessage("*palette*");
    }

    [Fact]
    public void Load_PaletteModeNotAnObject_NamesTheField()
    {
        WriteJson("""{ "palette": { "light": 5 } }""");

        var act = () => Load();

        act.Should().Throw<BrandingException>().WithMessage("*palette.light*");
    }

    [Fact]
    public void Load_ContrastBelowAa_LogsOneWarningPerPairNamingModePairAndRatio()
    {
        // The default light bg #F8FAFC against this muted grey is below 4.5:1.
        WriteJson("""{ "palette": { "light": { "muted": "#94A3B8" } } }""");

        Load();

        // Brief 13g item 7: the list holds every pair the CSS combines, so a pale muted grey now also fails
        // on the surface and the sidebar (and as a boundary against both): one warning per pair, all of them muted.
        _log.Warnings.Should().OnlyContain(w => w.Contains("light") && w.Contains("muted/"), "only muted pairs in the light palette are below their ratio");
        var warning = _log.Warnings.Single(w => w.Contains("muted/bg") && w.Contains("below 4.5:1"));
        warning.Should().Contain("light", "the warning names the mode");
        warning.Should().MatchRegex(@"\d+\.\d{2}:1", "the warning names the ratio");
        _log.Warnings.Should().Contain(w => w.Contains("muted/surface"), "the review's missing pair is checked");
    }

    [Fact]
    public void Load_AlphaColorInAPair_SkipsTheContrastCheck()
    {
        // An alpha colour's contrast depends on what is behind it, so the pair is skipped.
        WriteJson("""{ "palette": { "light": { "muted": "rgba(148,163,184,.5)" } } }""");

        Load();

        _log.Warnings.Should().BeEmpty("alpha colours are skipped");
    }

    [Fact]
    public void Load_EveryDefaultPairPassesAa_InBothModes()
    {
        // The warnings are the observable outcome: the built-in defaults must never produce one.
        Load();

        _log.Warnings.Should().BeEmpty("the default palette passes every listed pair in both modes");
    }

    [Fact]
    public void Load_SuppliedImages_AreRecordedWithTheContentHashOfEachServedFile()
    {
        WriteFile(_siteFolder, "logo.svg", "<svg>site logo</svg>");
        WriteFile(_defaultsFolder, "logo.svg", "<svg>default logo</svg>");
        WriteFile(_defaultsFolder, "favicon.ico", "default favicon");
        WriteFile(_defaultsFolder, "apple-touch-icon.png", "default touch icon");
        WriteFile(_defaultsFolder, "fallback-banner.svg", "<svg>default banner</svg>");

        var branding = Load().Current;

        branding.SuppliedFiles.Should().BeEquivalentTo("logo.svg");
        branding.FallbackBannerFile.Should().Be("fallback-banner.svg",
            "the site supplies no fallback banner, so the default svg is served");
        branding.FileHashes.Should().ContainKeys("logo.svg", "favicon.ico", "apple-touch-icon.png", "fallback-banner.svg");
        branding.FileHashes["logo.svg"].Should().Be(Sha256Prefix("<svg>site logo</svg>"),
            "the site's file wins over the default");
        branding.FileHashes["favicon.ico"].Should().Be(Sha256Prefix("default favicon"),
            "an absent site file is served from the defaults");
    }

    [Fact]
    public void Load_FallbackBanner_TakesTheFirstOfSvgPngJpgTheSiteSupplies()
    {
        WriteFile(_siteFolder, "fallback-banner.jpg", "jpg banner");
        WriteFile(_siteFolder, "fallback-banner.png", "png banner");
        WriteFile(_defaultsFolder, "fallback-banner.svg", "<svg>default banner</svg>");

        var branding = Load().Current;

        branding.FallbackBannerFile.Should().Be("fallback-banner.png", "png beats jpg; the site's svg is absent");
        branding.FileHashes["fallback-banner.png"].Should().Be(Sha256Prefix("png banner"));
        branding.FileHashes.Should().NotContainKey("fallback-banner.svg", "only the served file is hashed");
    }

    [Fact]
    public void Load_Masthead_DefaultsToTrueAndHasNoBannerFile()
    {
        WriteJson("""{ "name": "Field Notes" }""");

        var branding = Load().Current;

        branding.Masthead.Should().BeTrue("the home page masthead is on unless the site turns it off");
        branding.MastheadBannerFile.Should().BeNull("no masthead-banner file means the masthead uses the fallback banner");
    }

    [Fact]
    public void Load_PoweredBy_DefaultsToShownWithNoUrl()
    {
        WriteJson("""{ "name": "Field Notes" }""");

        var branding = Load().Current;

        branding.ShowPoweredBy.Should().BeTrue("the footer's 'Running on Croft' line is on unless the site turns it off");
        branding.PoweredByUrl.Should().BeEmpty("no URL means 'Croft' is plain text");
    }

    [Fact]
    public void Load_PoweredByFields_AreRead()
    {
        WriteJson("""{ "showPoweredBy": false, "poweredByUrl": "https://github.com/example/croft" }""");

        var branding = Load().Current;

        branding.ShowPoweredBy.Should().BeFalse();
        branding.PoweredByUrl.Should().Be("https://github.com/example/croft");
    }

    [Theory]
    [InlineData("showPoweredBy", "\"yes\"", "*showPoweredBy*boolean*")]
    [InlineData("showPoweredBy", "null", "*showPoweredBy*boolean*")]
    [InlineData("poweredByUrl", "5", "*poweredByUrl*string*")]
    [InlineData("poweredByUrl", "\"javascript:alert(1)\"", "*poweredByUrl*http*")]
    [InlineData("poweredByUrl", "\"/relative\"", "*poweredByUrl*http*")]
    [InlineData("poweredByUrl", "\"github.com/example\"", "*poweredByUrl*http*")]
    public void Load_InvalidPoweredByField_FailsNamingTheField(string field, string value, string message)
    {
        WriteJson($$"""{ "{{field}}": {{value}} }""");

        var act = () => Load();

        act.Should().Throw<BrandingException>().WithMessage(message);
    }

    [Fact]
    public void Load_EmptyPoweredByUrl_IsAllowed()
    {
        WriteJson("""{ "poweredByUrl": "" }""");

        Load().Current.PoweredByUrl.Should().BeEmpty();
    }

    [Fact]
    public void Load_MastheadFalse_TurnsItOff()
    {
        WriteJson("""{ "masthead": false }""");

        Load().Current.Masthead.Should().BeFalse();
    }

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void Load_MastheadThatIsNotABoolean_FailsNamingTheField(string value)
    {
        WriteJson($$"""{ "masthead": {{value}} }""");

        var act = () => Load();

        act.Should().Throw<BrandingException>().WithMessage("*masthead*boolean*");
    }

    [Fact]
    public void Load_MastheadBanner_IsTheFirstOfSvgPngJpgTheSiteSuppliesAndIsHashed()
    {
        WriteFile(_siteFolder, "masthead-banner.jpg", "jpg masthead");
        WriteFile(_siteFolder, "masthead-banner.png", "png masthead");
        WriteFile(_defaultsFolder, "fallback-banner.svg", "<svg>default banner</svg>");

        var branding = Load().Current;

        branding.MastheadBannerFile.Should().Be("masthead-banner.png", "png beats jpg; the site's svg is absent");
        branding.FileHashes["masthead-banner.png"].Should().Be(Sha256Prefix("png masthead"));
        branding.FileHashes.Should().NotContainKey("masthead-banner.jpg", "only the served file is hashed");
        branding.SuppliedFiles.Should().Contain("masthead-banner.png");
    }

    [Fact]
    public void Load_SiteCss_IsSuppliedAndHashedByItsBytes()
    {
        WriteFile(_siteFolder, "site.css", ":root { --primary: red; }");

        var service = Load();

        service.Current.SuppliedFiles.Should().Contain("site.css");
        service.Current.FileHashes["site.css"].Should().Be(Sha256Prefix(":root { --primary: red; }"));
        service.ResolveFile("site.css").Should().Be(Path.Combine(_siteFolder, "site.css"));
    }

    [Fact]
    public void Load_SiteCss_IsNotTakenFromTheFrameworkDefaults()
    {
        WriteFile(_defaultsFolder, "site.css", "/* stray default */");

        var service = Load();

        service.Current.SuppliedFiles.Should().NotContain("site.css");
        service.Current.FileHashes.Should().NotContainKey("site.css");
        service.ResolveFile("site.css").Should().BeNull("there is no framework default stylesheet");
    }

    [Fact]
    public void Load_SiteCss_WithNoFolder_IsNotServed()
    {
        var service = BrandingService.Load(null, _defaultsFolder, _log);

        service.ResolveFile("site.css").Should().BeNull();
    }

    [Fact]
    public void Load_MastheadBanner_IsNotTakenFromTheFrameworkDefaults()
    {
        // Unlike the fallback banner there is no framework default masthead file: unset means
        // "use the fallback banner", even when the defaults folder happens to hold one.
        WriteFile(_defaultsFolder, "masthead-banner.svg", "<svg>stray default</svg>");

        Load().Current.MastheadBannerFile.Should().BeNull();
    }

    [Fact]
    public void ThemeCss_MastheadBannerToken_AppearsOnlyWhenTheSiteSuppliesTheFile_AndMovesWithTheFile()
    {
        WriteFile(_defaultsFolder, "fallback-banner.svg", "<svg>fb</svg>");
        var without = Load().ThemeCss;
        WriteFile(_siteFolder, "masthead-banner.png", "one");
        var one = Load().ThemeCss;
        WriteFile(_siteFolder, "masthead-banner.png", "two");
        var two = Load().ThemeCss;

        without.Should().NotContain("--masthead-banner", "unset: the masthead CSS falls back to --fallback-banner");
        one.Should().Contain($"--masthead-banner: url('/branding/masthead-banner.png?v={Sha256Prefix("one")}');");
        two.Should().NotBe(one, "swapping the file moves theme.css's text and with it the cache-buster");
    }


    [Fact]
    public void ThemeCss_CarriesTheFallbackBannerUrl_SoASwappedBannerMovesTheServedTextAndItsVersion()
    {
        // theme.css is cached immutable for a year under ?v= (ThemeStylesheet.Version, the hash of
        // the served text). It carries the fallback banner's content-hashed URL
        // (--fallback-banner), so swapping the banner file must change the text and with it the
        // version — or browsers keep the old theme.css and its stale banner URL.
        WriteFile(_defaultsFolder, "fallback-banner.svg", "<svg>one</svg>");
        var first = Load();
        WriteFile(_defaultsFolder, "fallback-banner.svg", "<svg>two</svg>");
        var second = Load();

        second.ThemeCss.Should().NotBe(first.ThemeCss, "the banner file's content hash is part of theme.css");
        var firstSheet = new Croft.Web.Theme.ThemeStylesheet();
        var secondSheet = new Croft.Web.Theme.ThemeStylesheet();
        firstSheet.Initialize(first.ThemeCss);
        secondSheet.Initialize(second.ThemeCss);
        secondSheet.Version.Should().NotBe(firstSheet.Version, "the cache-buster moves with the banner");
    }

    /// <summary>Brief 11a item 4: the owner's profiles parse in the owner's order, keeping each
    /// service and exact URL; the fediverse handle parses alongside them.</summary>
    [Fact]
    public void Load_Profiles_ParseInOwnerOrder_WithFediverseCreator()
    {
        WriteJson(
            """
            {
              "profiles": [
                { "service": "mastodon", "url": "https://example.social/@someone" },
                { "service": "bluesky", "url": "https://bsky.app/profile/example.com" },
                { "service": "github", "url": "https://github.com/someone" },
                { "service": "linkedin", "url": "https://www.linkedin.com/in/someone" }
              ],
              "fediverseCreator": "@someone@example.social"
            }
            """);

        var branding = Load().Current;

        BrandProfile[] expected =
        [
            new("mastodon", "https://example.social/@someone"),
            new("bluesky", "https://bsky.app/profile/example.com"),
            new("github", "https://github.com/someone"),
            new("linkedin", "https://www.linkedin.com/in/someone"),
        ];
        branding.Profiles.Should().Equal(
            expected,
            "the owner's order is kept and each entry keeps its service and exact URL");
        branding.FediverseCreator.Should().Be("@someone@example.social");
    }

    /// <summary>Brief 11a item 4: both fields are optional — without them nothing renders.</summary>
    [Fact]
    public void Load_WithoutProfilesOrFediverseCreator_LeavesBothEmpty()
    {
        WriteJson("""{ "name": "Field Notes" }""");

        var branding = Load().Current;

        branding.Profiles.Should().BeEmpty();
        branding.FediverseCreator.Should().BeEmpty();
    }

    /// <summary>Brief 11a item 4: only mastodon, bluesky, github and linkedin are accepted
    /// services; anything else fails startup, naming the field.</summary>
    [Fact]
    public void Load_UnknownProfileService_FailsStartup_NamingTheField()
    {
        WriteJson("""{ "profiles": [ { "service": "tumblr", "url": "https://example.social/@someone" } ] }""");

        var act = () => Load();

        act.Should().Throw<BrandingException>()
            .WithMessage("*profiles*service*tumblr*not a known service*");
    }

    /// <summary>Brief 11a item 4: a profile URL must be absolute https (it becomes a rel="me"
    /// href); a plain http URL fails startup.</summary>
    [Fact]
    public void Load_ProfileHttpUrl_FailsStartup()
    {
        WriteJson("""{ "profiles": [ { "service": "github", "url": "http://github.com/someone" } ] }""");

        var act = () => Load();

        act.Should().Throw<BrandingException>()
            .WithMessage("*profiles*must be an absolute https URL*");
    }

    /// <summary>Brief 11a item 4: fediverseCreator must look like @user@host; the bare
    /// someone@host form fails startup.</summary>
    [Fact]
    public void Load_FediverseCreatorWithoutLeadingAt_FailsStartup()
    {
        WriteJson("""{ "fediverseCreator": "someone@example.social" }""");

        var act = () => Load();

        act.Should().Throw<BrandingException>()
            .WithMessage("*fediverseCreator*must look like @user@host*");
    }
}
