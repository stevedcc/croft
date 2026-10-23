namespace Croft.Web.E2E;

/// <summary>
/// Brief 11a item 1: the site-files folder (security.txt, humans.txt, WebFinger and similar
/// well-known files) is served at the URL path that matches the file's relative path. The dev
/// and E2E stacks mount site-files.example/, so <c>/.well-known/security.txt</c> and
/// <c>/humans.txt</c> return their example files. Anonymous and engine-agnostic.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
[Category("SiteFiles")]
public class SiteFilesTests : WebTest
{
    private static HttpClient CreateClient() => new(new HttpClientHandler
    {
        // No TLS on the agent stack (plain http://localhost): the E2E suite talks to it over
        // HTTP and the site-files endpoint answers with public, max-age=3600 regardless.
        UseCookies = false,
    });

    [Test]
    public async Task SecurityTxt_ReturnsTheExampleFile()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync($"{BaseUrl}/.well-known/security.txt");

        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsByteArrayAsync(),
            Is.EqualTo(await File.ReadAllBytesAsync(ExampleFile(".well-known", "security.txt"))),
            "the stack serves the example file's exact bytes");
        Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("text/plain"));
        Assert.That(response.Headers.CacheControl!.ToString(), Is.EqualTo("public, max-age=3600"));
        Assert.That(response.Headers.ETag, Is.Not.Null);
        Assert.That(response.Headers.Contains("Set-Cookie"), Is.False);
        Assert.That(response.Headers.Contains("Content-Security-Policy"), Is.False);
    }

    [Test]
    public async Task HumansTxt_ReturnsTheExampleFile()
    {
        using var client = CreateClient();

        using var response = await client.GetAsync($"{BaseUrl}/humans.txt");

        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK));
        Assert.That(await response.Content.ReadAsByteArrayAsync(),
            Is.EqualTo(await File.ReadAllBytesAsync(ExampleFile("humans.txt"))),
            "the stack serves the example file's exact bytes");
        Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("text/plain"));
    }

    /// <summary>A file of the repository's site-files.example/ folder, the folder the dev and E2E stacks serve.</summary>
    private static string ExampleFile(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Croft.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.That(dir, Is.Not.Null, "run these tests through dev/run-e2e.sh, from inside the repository");
        return Path.Combine([dir!.FullName, "site-files.example", .. relative]);
    }
}
