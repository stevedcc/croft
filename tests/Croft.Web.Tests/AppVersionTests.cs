using System.Xml.Linq;
using AwesomeAssertions;

namespace Croft.Web.Tests;

/// <summary>Brief 10f item 2: the footer's "Running on Croft vX.Y.Z" shows the released version,
/// stamped at build from the one <c>&lt;Version&gt;</c> in Directory.Build.props.</summary>
public class AppVersionTests
{
    [Theory]
    [InlineData("0.6.1+3af8a1b0c2d4e5f6", "0.6.1")]
    [InlineData("0.6.1", "0.6.1")]
    [InlineData("1.0.0-rc.1+abc", "1.0.0-rc.1")]
    [InlineData("", "")]
    public void WithoutBuildMetadata_DropsThePlusCommitSuffix(string informational, string expected) =>
        AppVersion.WithoutBuildMetadata(informational).Should().Be(expected);

    [Fact]
    public void Current_HasNoCommitSuffix_AndEqualsTheVersionInDirectoryBuildProps()
    {
        AppVersion.Current.Should().NotContain("+", "the commit suffix is build metadata, not the release");
        AppVersion.Current.Should().Be(VersionFromProps(),
            "the footer shows the one <Version> the release bumps");
    }

    private static string VersionFromProps()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("Directory.Build.props sits at the repository root");
        var props = XDocument.Load(Path.Combine(directory!.FullName, "Directory.Build.props"));
        return props.Descendants("Version").Single().Value;
    }
}
