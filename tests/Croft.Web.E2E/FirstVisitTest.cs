using System.Text.RegularExpressions;

namespace Croft.Web.E2E;

/// <summary>
/// Shared plumbing for the brief-14c tests of what a first visit downloads: a post with bold, italic,
/// inline code, a heading and two images, in a category with a tag, published through the API as the
/// mock admin (run-keyed, so no test depends on database state), and a measurement of one page load
/// in a fresh browser context (an empty cache), read from the browser's own network events.
/// </summary>
public abstract class FirstVisitTest : StaticReadingTest
{
    protected static readonly (int Width, int Height)[] Viewports = [(1440, 900), (390, 844)];

    /// <summary>One run's pages: the home page, a post, a category and a tag.</summary>
    protected sealed record Fixtures(string PostPath, string CategoryPath, string TagPath)
    {
        public IEnumerable<string> Paths => ["/", PostPath, CategoryPath, TagPath];
    }

    /// <summary>One font face the browser reports: family (unquoted), weight and style.</summary>
    protected sealed record Face(string Family, string Weight, string Style)
    {
        public override string ToString() => $"{Family} {Weight} {Style}";
    }

    /// <summary>One load of one page in an empty browser cache.</summary>
    protected sealed record Visit(
        string Path, int Width, int Requests, long TotalBytes,
        IReadOnlyDictionary<string, long> BytesByKind, IReadOnlyDictionary<string, int> RequestsByKind,
        IReadOnlyList<string> FontUrls, IReadOnlyList<(string Url, int Status, string? CacheControl)> FontResponses,
        IReadOnlyList<Face> LoadedFaces, IReadOnlyList<string> ConsoleWarnings, IReadOnlyList<string> PreloadUrls)
    {
        public long FontBytes => BytesByKind.GetValueOrDefault("font");

        public override string ToString() =>
            $"{Path} @{Width}: {Requests} requests, {TotalBytes} bytes; " +
            string.Join(", ", BytesByKind.OrderBy(k => k.Key).Select(k => $"{k.Key} {RequestsByKind[k.Key]}x/{k.Value} B")) +
            $"; fonts [{string.Join(", ", FontUrls.Select(u => u[(u.LastIndexOf('/') + 1)..]))}]";
    }

    private Fixtures? _fixtures;

    protected string RunId { get; } = Guid.NewGuid().ToString("N")[..10];

    /// <summary>A post with bold, italic, inline code, a heading and two images, in a category with a
    /// tag; the pages that show it. Built once per fixture class, after the instance is pinned.</summary>
    protected async Task<Fixtures> EnsureFixturesAsync()
    {
        if (_fixtures is not null)
        {
            return _fixtures;
        }

        var first = await UploadSolidImageAsync($"first-visit-a-{RunId}.png", "#33557A");
        var second = await UploadSolidImageAsync($"first-visit-b-{RunId}.png", "#7A5533");
        var category = await CreateCategoryAsync($"First visit {RunId}");
        var tag = $"firstvisit{RunId}";
        var body =
            $"An opening paragraph with *italic* words, **bold** words and `inline_code()` in it, {RunId}.\n\n" +
            $"![First picture](/api/media/{first})\n\n" +
            "## A section\n\n" +
            $"{Sentence}. {Sentence}.\n\n" +
            $"![Second picture](/api/media/{second})\n\n" +
            $"{Sentence}.\n";
        var post = await PublishAsync($"First visit post {RunId}", body, tags: [tag], categoryId: category.Id);
        _fixtures = new Fixtures($"/post/{post.Slug}", $"/category/{category.Slug}", $"/tag/{tag}");
        return _fixtures;
    }

    /// <summary>Uploads a 1200 x 675 PNG of one colour as the mock admin; returns the media id.</summary>
    private async Task<Guid> UploadSolidImageAsync(string fileName, string colour)
    {
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = 1200, Height = 675 }
        });
        var page = await context.NewPageAsync();
        await page.SetContentAsync($"<style>html, body {{ margin: 0; background: {colour}; }}</style>");
        var png = await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Png });
        using var client = MockIdpApi.CreateClient();
        var token = await MockIdpApi.GetAdminAccessTokenAsync(client);
        return await MockIdpApi.UploadImageAsync(client, ApiUrl, fileName, png, token);
    }

    /// <summary>Loads <paramref name="path"/> in a fresh context (an empty cache) at the viewport and
    /// records what the network carried: after <c>document.fonts.ready</c> and network idle.</summary>
    protected async Task<Visit> VisitAsync(
        string path, int width, int height, string acceptEncoding = "gzip, deflate, br, zstd", bool watchPreloadWarning = false)
    {
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = width, Height = height },
            HasTouch = width < 600,
            ExtraHTTPHeaders = new Dictionary<string, string> { ["Accept-Encoding"] = acceptEncoding },
        });
        var page = await context.NewPageAsync();
        using var responseLog = EventLog.Responses(page);
        using var warningLog = EventLog.ConsoleTexts(page, "warning", "error");

        await page.GotoAsync($"{BaseUrl}{path}");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.EvaluateAsync("() => document.fonts.ready");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        if (watchPreloadWarning)
        {
            // Chromium warns about an unused preload a few seconds after the window's load event: an
            // absent warning can only be observed by waiting for it, so this is a bounded wait for a
            // message that must not come (the break-it-once preloads a face the page does not use).
            try
            {
                await page.WaitForConsoleMessageAsync(new PageWaitForConsoleMessageOptions
                {
                    Predicate = message => message.Text.Contains("preloaded using link preload but not used", StringComparison.Ordinal),
                    Timeout = 6000,
                });
            }
            catch (TimeoutException)
            {
                // no such warning: the preloads were all used
            }
        }

        responseLog.Stop();
        warningLog.Stop();
        var responses = responseLog.Snapshot();
        var warnings = warningLog.Snapshot();

        var preloads = await page.EvaluateAsync<string[]>(
            "() => [...document.querySelectorAll('link[rel=preload][as=font]')].map(l => l.href)");

        var bytesByKind = new Dictionary<string, long>();
        var requestsByKind = new Dictionary<string, int>();
        var fontUrls = new List<string>();
        var fontResponses = new List<(string, int, string?)>();
        long total = 0;
        foreach (var response in responses)
        {
            var kind = response.Request.ResourceType switch
            {
                "stylesheet" => "style",
                var other => other,
            };
            var size = (await response.Request.SizesAsync()).ResponseBodySize;
            total += size;
            bytesByKind[kind] = bytesByKind.GetValueOrDefault(kind) + size;
            requestsByKind[kind] = requestsByKind.GetValueOrDefault(kind) + 1;
            if (kind == "font")
            {
                fontUrls.Add(response.Url);
                fontResponses.Add((response.Url, response.Status, response.Headers.GetValueOrDefault("cache-control")));
            }
        }

        var faces = (await page.EvaluateAsync<string[][]>(
            @"() => [...document.fonts].filter(f => f.status === 'loaded')
                .map(f => [f.family.replace(/^[""']|[""']$/g, ''), String(f.weight), f.style])"))
            .Select(f => new Face(f[0], f[1], f[2])).ToList();
        return new Visit(path, width, responses.Count, total, bytesByKind, requestsByKind, fontUrls, fontResponses, faces, warnings, preloads);
    }

    /// <summary>The face each shipped font file holds (dev/fonts/subset-fonts.py names them).</summary>
    protected static readonly IReadOnlyDictionary<string, Face> FaceOfFile = new Dictionary<string, Face>
    {
        ["CroftSerif-Regular"] = new("Croft Serif", "400", "normal"),
        ["CroftSerif-Italic"] = new("Croft Serif", "400", "italic"),
        ["CroftSerif-SemiBold"] = new("Croft Serif", "600", "normal"),
        ["CroftSerif-Bold"] = new("Croft Serif", "700", "normal"),
        ["CroftSans-Regular"] = new("Croft Sans", "400", "normal"),
        ["CroftSans-SemiBold"] = new("Croft Sans", "600", "normal"),
        ["JetBrainsMono-Latin"] = new("JetBrains Mono", "400", "normal"),
    };

    /// <summary>The path part of a URL.</summary>
    protected static string PathOf(string url) => new Uri(url).AbsolutePath;

    /// <summary>The file name of a font URL without its fingerprint: <c>CroftSerif-Regular</c>.</summary>
    protected static string FaceFile(string url) =>
        Regex.Replace(Path.GetFileNameWithoutExtension(PathOf(url)), @"\.[A-Za-z0-9_-]{10}$", "");
}
