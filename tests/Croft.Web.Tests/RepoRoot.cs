using AwesomeAssertions;

namespace Croft.Web.Tests;

/// <summary>Locates the repository root (the folder holding the .slnx) from the test binaries.</summary>
internal static class RepoRoot
{
    public static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && Directory.GetFiles(dir.FullName, "*.slnx").Length == 0)
        {
            dir = dir.Parent!;
        }

        dir.Should().NotBeNull("the tests run from inside the repository");
        return dir!.FullName;
    }
}
