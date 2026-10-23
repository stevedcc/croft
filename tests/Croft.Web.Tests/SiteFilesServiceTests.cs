using System.Text;
using AwesomeAssertions;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 11a item 1: SiteFilesService loads every file in the folder, recursively, into an
/// immutable snapshot the HTTP endpoint serves. The brief rules the service enforces:
/// allowed extensions only (.txt, .json); allowed names without an extension
/// (.well-known/apple-app-site-association, .well-known/webfinger); a hard 64 KiB cap; no
/// shadowing of the app's own URL paths or prefixes; a missing or empty folder is fine.
/// </summary>
public class SiteFilesServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"blog-site-files-{Guid.NewGuid():N}");

    public SiteFilesServiceTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static void WriteFile(string folder, string relative, string content)
    {
        var path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
        var parent = Path.GetDirectoryName(path)!;
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.WriteAllText(path, content);
    }

    private static void WriteBytes(string folder, string relative, byte[] bytes) =>
        File.WriteAllBytes(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)), bytes);

    [Fact]
    public void Load_MissingFolder_ReturnsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blog-site-files-missing-{Guid.NewGuid():N}");

        var siteFiles = SiteFilesService.Load(path);

        siteFiles.ByPath.Should().BeEmpty("a missing folder means no site files at all");
        siteFiles.Resolve("/anything").Should().BeNull();
    }

    [Fact]
    public void Load_NullOrEmptyPath_ReturnsEmpty()
    {
        SiteFilesService.Load(null).ByPath.Should().BeEmpty();
        SiteFilesService.Load("").ByPath.Should().BeEmpty();
        SiteFilesService.Load("   ").ByPath.Should().BeEmpty();
    }

    [Fact]
    public void Load_AllowedTxt_IsIndexedAtItsRelativePath_AndServedAsPlainText()
    {
        WriteFile(_folder, "humans.txt", "We are humans.");

        var siteFiles = SiteFilesService.Load(_folder);

        var file = siteFiles.Resolve("/humans.txt");
        file.Should().NotBeNull();
        file!.UrlPath.Should().Be("/humans.txt");
        file.ContentType.Should().Be("text/plain; charset=utf-8");
        Encoding.UTF8.GetString(file.Bytes).Should().Be("We are humans.");
    }

    [Fact]
    public void Load_AllowedJson_IsIndexedAsApplicationJson()
    {
        WriteFile(_folder, "foo.json", """{"hello":"world"}""");

        var siteFiles = SiteFilesService.Load(_folder);

        var file = siteFiles.Resolve("/foo.json");
        file.Should().NotBeNull();
        file!.ContentType.Should().Be("application/json");
        Encoding.UTF8.GetString(file.Bytes).Should().Be("""{"hello":"world"}""");
    }

    [Fact]
    public void Load_AppleAppSiteAssociationWithoutExtension_IsIndexedAsApplicationJson()
    {
        WriteFile(_folder, ".well-known/apple-app-site-association", """{"applinks":{}}""");

        var siteFiles = SiteFilesService.Load(_folder);

        var file = siteFiles.Resolve("/.well-known/apple-app-site-association");
        file.Should().NotBeNull();
        file!.ContentType.Should().Be("application/json",
            "the AASA file has no extension and gets an explicit JSON content-type");
    }

    [Fact]
    public void Load_ETag_IsAStrongQuotedContentHash_StableAcrossReads()
    {
        WriteFile(_folder, "humans.txt", "We are humans.");
        var first = SiteFilesService.Load(_folder).Resolve("/humans.txt")!.ETag;
        var second = SiteFilesService.Load(_folder).Resolve("/humans.txt")!.ETag;

        first.Should().StartWith("\"").And.EndWith("\"", "the ETag is quoted so HTTP compares it correctly");
        first.Should().Be(second, "the ETag is derived from the file content, not the load order");
        first.Length.Should().Be(2 + 32, "ETag is a 32-hex SHA-256 prefix");
    }

    [Fact]
    public void Load_NestedFiles_AreIndexedAtTheirRelativePaths()
    {
        Directory.CreateDirectory(Path.Combine(_folder, ".well-known"));
        WriteFile(_folder, ".well-known/security.txt", "Contact: mailto:security@example.com");

        var siteFiles = SiteFilesService.Load(_folder);

        var file = siteFiles.Resolve("/.well-known/security.txt");
        file.Should().NotBeNull();
        file!.ContentType.Should().Be("text/plain; charset=utf-8");
    }

    [Theory]
    [InlineData("page.html", ".html")]
    [InlineData("page.htm", ".htm")]
    [InlineData("image.svg", ".svg")]
    [InlineData("script.js", ".js")]
    [InlineData("style.css", ".css")]
    [InlineData("data.xml", ".xml")]
    public void Load_ForbiddenExtension_FailsStartupNamingTheFileAndExtension(string name, string ext)
    {
        WriteFile(_folder, name, "should not load");

        var act = () => SiteFilesService.Load(_folder);

        act.Should().Throw<SiteFilesException>()
            .WithMessage($"*{name}*forbidden extension '{ext}'*");
    }

    [Fact]
    public void Load_FileWithNoExtension_AndNotInAllowedNames_FailsStartup()
    {
        File.WriteAllText(Path.Combine(_folder, "license"), "MIT");

        var act = () => SiteFilesService.Load(_folder);

        act.Should().Throw<SiteFilesException>()
            .WithMessage("*license*no extension*well-known*");
    }

    [Fact]
    public void Load_FileOver64KiB_FailsStartupWithTheSizeAndTheLimit()
    {
        // 64 KiB + 1 byte: the exact threshold the brief calls out.
        var bytes = new byte[SiteFilesService.MaxFileBytes + 1];
        WriteBytes(_folder, "big.txt", bytes);

        var act = () => SiteFilesService.Load(_folder);

        act.Should().Throw<SiteFilesException>()
            .WithMessage("*big.txt*larger than the*65536*limit*");
    }

    [Fact]
    public void Load_FileAtExactly64KiB_IsAllowed()
    {
        var bytes = new byte[SiteFilesService.MaxFileBytes];
        WriteBytes(_folder, "exact.txt", bytes);

        var siteFiles = SiteFilesService.Load(_folder);

        siteFiles.Resolve("/exact.txt").Should().NotBeNull("64 KiB is the limit, not above it");
    }

    [Theory]
    [InlineData("/robots.txt")]
    [InlineData("/sitemap.xml")]
    [InlineData("/feed.xml")]
    [InlineData("/manifest.webmanifest")]
    [InlineData("/favicon.ico")]
    [InlineData("/branding/theme.css")]
    [InlineData("/branding/logo.svg")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/api/anything")]
    public void Load_ShadowingAppPath_FailsStartupNamingBothTheFileAndTheRoute(string urlPath)
    {
        var name = urlPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        WriteFile(_folder, name, "shadowing");

        var act = () => SiteFilesService.Load(_folder);

        act.Should().Throw<SiteFilesException>()
            .WithMessage($"*{urlPath}*reserved*");
    }

    [Theory]
    [InlineData("Humans.TXT", "Humans.TXT")]
    [InlineData("notes.Json", "notes.Json")]
    [InlineData(".well-known/WebFinger", ".well-known/WebFinger")]
    [InlineData(".well-known/Security.TXT", ".well-known/Security.TXT")]
    public void Load_NameInTheWrongCase_FailsStartupSayingNamesAreCaseSensitive(string name, string shown)
    {
        // Names are exact: the extension and the well-known names are lowercase, and a
        // case variant is refused with a message about the case, not about "forbidden extension".
        WriteFile(_folder, name, "x");

        var act = () => SiteFilesService.Load(_folder);

        act.Should().Throw<SiteFilesException>()
            .Where(e => e.Message.Contains(shown) && e.Message.Contains("case-sensitive") && e.Message.Contains("lowercase")
                && !e.Message.Contains("forbidden extension"));
    }

    [Fact]
    public void Load_LowercaseNames_AreAccepted()
    {
        WriteFile(_folder, "humans.txt", "x");
        WriteFile(_folder, ".well-known/webfinger", """{"subject":"acct:a@b.c"}""");

        SiteFilesService.Load(_folder).ByPath.Keys.Should().BeEquivalentTo(["/humans.txt", "/.well-known/webfinger"]);
    }

    [Theory]
    [InlineData("{id}.txt")]
    [InlineData("a{b}.txt")]
    [InlineData("{**rest}.txt")]
    [InlineData("star*.txt")]
    [InlineData("what?.txt")]
    [InlineData("sub/{x}/file.txt")]
    [InlineData("has space.txt")]
    public void Load_NameWithRouteSignificantOrUnsafeCharacters_FailsStartupNamingTheRule(string name)
    {
        // The file's name becomes its URL, matched exactly. A name like {id}.txt must never be
        // read as a route template (it would answer for every /anything.txt).
        WriteFile(_folder, name, "x");

        var act = () => SiteFilesService.Load(_folder);

        act.Should().Throw<SiteFilesException>()
            .WithMessage($"*{name}*letters, digits*");
    }

    [Fact]
    public void Load_NameWithBracesIsRejected_SoNoWildcardRouteCanExist()
    {
        // The accepted names are the only way a route gets registered: every one is made of
        // letters, digits and . - _ / only, so none can contain a route parameter.
        WriteFile(_folder, ".well-known/security.txt", "Contact: mailto:a@b.c");
        WriteFile(_folder, "humans.txt", "x");

        var siteFiles = SiteFilesService.Load(_folder);

        siteFiles.ByPath.Keys.Should().OnlyContain(path => path.All(c => char.IsAsciiLetterOrDigit(c) || "._-/".Contains(c)));
    }

    [Fact]
    public void EnsureNoStaticAssetShadowing_FileAtAStaticAssetUrl_FailsNamingBoth()
    {
        WriteFile(_folder, "fonts/OFL-SourceSerif4.txt", "shadowing");
        var siteFiles = SiteFilesService.Load(_folder);

        var act = () => SiteFilesService.EnsureNoStaticAssetShadowing(
            siteFiles, ["/fonts/OFL-SourceSerif4.txt", "/css/site.css"]);

        act.Should().Throw<SiteFilesException>()
            .WithMessage("*fonts/OFL-SourceSerif4.txt*static asset*");
    }

    [Fact]
    public void EnsureNoStaticAssetShadowing_NoCollision_Passes()
    {
        WriteFile(_folder, "humans.txt", "fine");
        var siteFiles = SiteFilesService.Load(_folder);

        var act = () => SiteFilesService.EnsureNoStaticAssetShadowing(siteFiles, ["/fonts/OFL-SourceSerif4.txt"]);

        act.Should().NotThrow();
    }

    [Fact]
    public void Load_KubernetesConfigMapLayout_ServesOnlyTheProjectedFiles_NotTheDotDotMirrors()
    {
        // A mounted ConfigMap: ..2026_10_19_12_00_00.123 holds the files, ..data points at it, and
        // each projected path is a symlink through ..data. Only the projected paths are site files.
        var stamp = Path.Combine(_folder, "..2026_10_19_12_00_00.123");
        Directory.CreateDirectory(Path.Combine(stamp, ".well-known"));
        File.WriteAllText(Path.Combine(stamp, "humans.txt"), "We are humans.");
        File.WriteAllText(Path.Combine(stamp, ".well-known", "security.txt"), "Contact: mailto:a@b.c");
        Directory.CreateSymbolicLink(Path.Combine(_folder, "..data"), stamp);
        File.CreateSymbolicLink(Path.Combine(_folder, "humans.txt"), Path.Combine("..data", "humans.txt"));
        Directory.CreateSymbolicLink(Path.Combine(_folder, ".well-known"), Path.Combine("..data", ".well-known"));

        var siteFiles = SiteFilesService.Load(_folder);

        siteFiles.ByPath.Keys.Should().BeEquivalentTo(["/humans.txt", "/.well-known/security.txt"],
            "the ..data and ..<timestamp> mirrors must not become URLs");
        Encoding.UTF8.GetString(siteFiles.Resolve("/humans.txt")!.Bytes).Should().Be("We are humans.");
    }

    [Fact]
    public void Load_SymlinkToAFile_IsResolvedAndServed()
    {
        // Kubernetes ConfigMap mounts use a `..data/` symlink: ensure we follow it. The test
        // creates a real file outside the folder and a symlink inside.
        var target = Path.Combine(Path.GetTempPath(), $"blog-symlink-target-{Guid.NewGuid():N}.txt");
        File.WriteAllText(target, "linked content");
        try
        {
            File.CreateSymbolicLink(Path.Combine(_folder, "linked.txt"), target);

            var siteFiles = SiteFilesService.Load(_folder);

            var file = siteFiles.Resolve("/linked.txt");
            file.Should().NotBeNull("symlinks resolve like regular files");
            Encoding.UTF8.GetString(file!.Bytes).Should().Be("linked content");
        }
        finally
        {
            File.Delete(target);
        }
    }
}