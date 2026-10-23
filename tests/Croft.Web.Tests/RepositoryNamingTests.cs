using System.Text.RegularExpressions;
using System.Xml.Linq;
using AwesomeAssertions;

namespace Croft.Web.Tests;

/// <summary>
/// The product prefix is Croft. Nothing in the repository may still name a Blog.* project:
/// no path segment, no text, and the solution file lists exactly the six Croft.* projects.
/// The domain word "blog" (cookies, config keys, k8s resource names, folders like
/// Components/Blog) is not a project name and is out of scope here. The regex literals below
/// spell the name with a backslash before the dot, so their own source never satisfies them;
/// this file's own namespace still names the project until the rename turns it red-to-green.
/// </summary>
public class RepositoryNamingTests
{
    /// <summary>Build output, vendored trees, agent scratch and history are not repository text.</summary>
    private static readonly string[] SkipSegments = [".git", ".superpowers", "bin", "obj", "node_modules", "uploads", "docs"];

    private static readonly Regex BlogProjectName = new(@"Blog\.(Api|Web|Shared)");

    private static readonly Regex BlogSolutionName = new(@"^Blog\.slnx$");

    private static readonly Regex BlogProjectMention = new(@"\bBlog\.(Api|Web|Shared)\b|Blog\.slnx");

    [Fact]
    public void NoPathSegment_NamesABlogProject()
    {
        var root = RepoRoot();
        var offenders = RepoFiles(root)
            .Where(path => Path.GetRelativePath(root, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => BlogProjectName.IsMatch(segment) || BlogSolutionName.IsMatch(segment)))
            .Select(path => Path.GetRelativePath(root, path))
            .Order()
            .ToList();

        offenders.Should().BeEmpty("no file name or path segment may name a Blog.* project");
    }

    [Fact]
    public void NoTextFile_MentionsABlogProjectOrTheBlogSolution()
    {
        var root = RepoRoot();
        var offenders = RepoFiles(root)
            .Where(path => IsText(path) && BlogProjectMention.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(root, path))
            .Order()
            .ToList();

        offenders.Should().BeEmpty("no text file may mention a Blog.* project or the old solution file");
    }

    [Fact]
    public void CroftSlnx_ListsExactlyTheSixCroftProjects()
    {
        var slnx = Path.Combine(RepoRoot(), "Croft.slnx");
        File.Exists(slnx).Should().BeTrue("the solution file is Croft.slnx");

        var projects = XDocument.Load(slnx)
            .Descendants("Project")
            .Select(p => p.Attribute("Path")?.Value)
            .Where(path => path is not null)
            .Select(path => Path.GetFileName(path))
            .Order()
            .ToList();

        projects.Should().BeEquivalentTo(
        [
            "Croft.Api.csproj",
            "Croft.Api.Tests.csproj",
            "Croft.Shared.csproj",
            "Croft.Web.csproj",
            "Croft.Web.E2E.csproj",
            "Croft.Web.Tests.csproj",
        ], "the solution lists exactly the six Croft.* projects");
    }

    private static IEnumerable<string> RepoFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(root, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .SkipLast(1)
                .Any(SkipSegments.Contains))
            .Order();

    private static bool IsText(string path) => !File.ReadAllBytes(path).Contains((byte)0);

    private static string RepoRoot()
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
