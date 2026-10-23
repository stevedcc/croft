using System.Net;
using Croft.Web.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 13a item 2: the not-found and error pages are static documents, so an anonymous visitor
/// who follows a dead link opens no Blazor circuit and loads no MudBlazor.
/// </summary>
public class StaticNotFoundPageTests
{
    [Theory]
    [InlineData("/no/such/page", HttpStatusCode.NotFound)]
    [InlineData("/not-found", HttpStatusCode.OK)]
    [InlineData("/Error", HttpStatusCode.OK)]
    [InlineData("/post/no-such-post", HttpStatusCode.NotFound)]
    public async Task NotFoundAndErrorDocuments_CarryNoCircuitAndNoMudBlazor(string path, HttpStatusCode status)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.test/");
            builder.UseSetting("Blog:BaseUrl", "https://blog.test/");
            builder.ConfigureServices(services =>
                services.AddHttpClient("BlogApi").ConfigurePrimaryHttpMessageHandler(() => new StubBlogApiHandler()));
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(status, path);
        BlazorCircuitClient.CollectSsrComponentMarkers(html).Should().Be("[]",
            $"{path} is a static document: it carries no interactive component to start a circuit with");
        html.Should().NotContain("_content/MudBlazor", $"{path} loads no MudBlazor");
    }
}
