using AwesomeAssertions;

namespace Croft.Api.Tests;

/// <summary>
/// Which command the API's command line asks for. A typo in a CronJob's args must never fall
/// through and start the web server (the Job would run forever and never report failure).
/// </summary>
public class StartupCommandTests
{
    [Theory]
    [InlineData]
    [InlineData("--urls", "http://localhost:5000")]
    [InlineData("--environment=Development")]
    public void NoArgumentsOrOnlyHostOptions_ServeTheApi(params string[] args) =>
        StartupCommand.Parse(args).Should().Be(new StartupCommand(StartupCommandKind.Serve, null));

    [Fact]
    public void Migrate_IsRecognised() =>
        StartupCommand.Parse(["migrate"]).Should().Be(new StartupCommand(StartupCommandKind.Migrate, null));

    [Fact]
    public void ExportWithOut_CarriesTheDirectory() =>
        StartupCommand.Parse(["export", "--out", "/backup/content"])
            .Should().Be(new StartupCommand(StartupCommandKind.Export, "/backup/content"));

    [Fact]
    public void ImportWithFrom_CarriesTheDirectory() =>
        StartupCommand.Parse(["import", "--from", "/backup/content"])
            .Should().Be(new StartupCommand(StartupCommandKind.Import, "/backup/content"));

    [Theory]
    [InlineData("bogus-command")]
    [InlineData("migrat")]
    [InlineData("migrate", "now")]
    [InlineData("export")]
    [InlineData("export", "--out")]
    [InlineData("export", "--from", "/x")]
    [InlineData("export", "--out", "/x", "extra")]
    [InlineData("import")]
    [InlineData("import", "--out", "/x")]
    [InlineData("import", "--from")]
    [InlineData("export", "--out", "")]
    public void AnythingElse_IsInvalid(params string[] args) =>
        StartupCommand.Parse(args).Kind.Should().Be(StartupCommandKind.Invalid);
}
