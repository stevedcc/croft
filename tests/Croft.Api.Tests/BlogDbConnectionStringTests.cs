using Croft.Api.Data;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 09d item 1: one helper builds every Croft.Api connection string from BlogDb with the
/// deployment's pool settings — Database:MaxPoolSize (default 20) and Database:MaxAutoPrepare
/// (default 0) — instead of Npgsql's defaults (100 per process).
/// </summary>
public class BlogDbConnectionStringTests
{
    [Fact]
    public void Build_WithoutPoolSettings_UsesTheDocumentedDefaults()
    {
        var built = BlogDbConnectionString.Build(Configuration()).ConnectionString;

        var parsed = new NpgsqlConnectionStringBuilder(built);
        parsed.MaxPoolSize.Should().Be(20, "Database:MaxPoolSize defaults to 20 per the brief's contracts");
        parsed.MaxAutoPrepare.Should().Be(0, "Database:MaxAutoPrepare defaults to 0 (Npgsql's default: off)");
    }

    [Fact]
    public void Build_WithPoolSettings_AppliesThemToTheBuiltString()
    {
        var configuration = Configuration(
            ("Database:MaxPoolSize", "3"),
            ("Database:MaxAutoPrepare", "10"));

        var built = BlogDbConnectionString.Build(configuration).ConnectionString;

        built.Should().Contain("Max Auto Prepare=10", "the setting must reach the built string");
        var parsed = new NpgsqlConnectionStringBuilder(built);
        parsed.MaxPoolSize.Should().Be(3);
        parsed.MaxAutoPrepare.Should().Be(10);
    }

    [Fact]
    public void Build_KeepsTheBaseConnectionDetailsAndApplicationName()
    {
        var configuration = Configuration(("Database:MaxPoolSize", "3"));

        var built = BlogDbConnectionString.Build(configuration).ConnectionString;

        var parsed = new NpgsqlConnectionStringBuilder(built);
        parsed.Host.Should().Be("db.example.internal");
        parsed.Port.Should().Be(5433);
        parsed.Database.Should().Be("blog");
        parsed.Username.Should().Be("blog_user");
        parsed.ApplicationName.Should().Be("pool-tests", "the caller's Application Name scoping survives");
    }

    [Fact]
    public void Build_WithoutBlogDb_Throws()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();

        var act = () => BlogDbConnectionString.Build(configuration);

        act.Should().Throw<InvalidOperationException>().WithMessage("*BlogDb*");
    }

    [Theory]
    [InlineData("Database:MaxPoolSize", "")]
    [InlineData("Database:MaxPoolSize", " ")]
    [InlineData("Database:MaxPoolSize", "twenty")]
    [InlineData("Database:MaxPoolSize", "0")]
    [InlineData("Database:MaxAutoPrepare", "")]
    [InlineData("Database:MaxAutoPrepare", "-1")]
    public void Build_WithInvalidPoolSetting_ThrowsNamingTheSetting(string key, string value)
    {
        var configuration = Configuration((key, value));

        var act = () => BlogDbConnectionString.Build(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{key}*", "the error must name the setting an operator has to fix");
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] overrides)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:BlogDb"] =
                "Host=db.example.internal;Port=5433;Database=blog;Username=blog_user;Password=secret;Application Name=pool-tests"
        };
        foreach (var (key, value) in overrides)
        {
            settings[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }
}
