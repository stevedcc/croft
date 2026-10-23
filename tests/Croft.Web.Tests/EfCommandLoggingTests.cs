using AwesomeAssertions;
using Microsoft.Extensions.Configuration;

namespace Croft.Web.Tests;

/// <summary>
/// Brief 09b item 7: "Microsoft.EntityFrameworkCore.Database.Command" logs every SQL statement
/// and is the bulk of all log volume, so it warns by default and keeps Information only where
/// someone is actively debugging — Development.
/// </summary>
public class EfCommandLoggingTests
{
    private const string Key = "Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command";

    [Fact]
    public void BothApps_WarnOnEfCommandsByDefault_AndKeepInformationInDevelopment()
    {
        foreach (var app in new[] { "Croft.Api", "Croft.Web" })
        {
            var appDir = Path.Combine(RepoRoot.Find(), "src", app);

            var production = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(appDir, "appsettings.json"))
                .Build();
            production[Key].Should().Be("Warning",
                $"{app} must not log every SQL statement at Information outside Development");

            var development = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(appDir, "appsettings.json"))
                .AddJsonFile(Path.Combine(appDir, "appsettings.Development.json"))
                .Build();
            development[Key].Should().Be("Information",
                $"{app}'s Development settings keep the SQL statements visible");
        }
    }
}
