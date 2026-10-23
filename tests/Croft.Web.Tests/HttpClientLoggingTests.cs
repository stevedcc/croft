using AwesomeAssertions;
using Microsoft.Extensions.Configuration;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 09d item 1: croft-web logged four System.Net.Http.HttpClient Information lines per API
/// call (~31 000 lines per pod in 30 minutes on 2026-10-15), so the category warns by default and
/// keeps Information only in Development.
/// </summary>
public class HttpClientLoggingTests
{
    private const string Key = "Logging:LogLevel:System.Net.Http.HttpClient";

    [Fact]
    public void CroftWeb_WarnsOnHttpClientCategoryByDefault_AndKeepsInformationInDevelopment()
    {
        var appDir = Path.Combine(RepoRoot.Find(), "src", "Croft.Web");

        var production = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(appDir, "appsettings.json"))
            .Build();
        production[Key].Should().Be("Warning",
            "croft-web must not log every HttpClient line at Information outside Development");

        var development = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(appDir, "appsettings.json"))
            .AddJsonFile(Path.Combine(appDir, "appsettings.Development.json"))
            .Build();
        development[Key].Should().Be("Information",
            "Development keeps the HttpClient lines visible for debugging");
    }
}
