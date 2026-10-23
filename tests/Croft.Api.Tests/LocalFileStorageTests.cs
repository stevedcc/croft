using AwesomeAssertions;
using Croft.Api.Services;

namespace Croft.Api.Tests;

/// <summary>
/// LocalFileStorage resolves a stored relative path under its base folder and refuses any path
/// that leaves it (brief 13e item 6, review M-11): by a <c>..</c> segment, by an absolute path, or
/// by naming a sibling folder whose name merely starts with the base folder's name. Paths come from
/// the database, so this is defence in depth.
/// </summary>
public class LocalFileStorageTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), $"blog-storage-{Guid.NewGuid():N}");

    public LocalFileStorageTests()
    {
        Directory.CreateDirectory(Path.Combine(_parent, "uploads", "global"));
        File.WriteAllText(Path.Combine(_parent, "uploads", "global", "inside.txt"), "inside");
        Directory.CreateDirectory(Path.Combine(_parent, "uploads-old"));
        File.WriteAllText(Path.Combine(_parent, "uploads-old", "secret.txt"), "secret");
        File.WriteAllText(Path.Combine(_parent, "outside.txt"), "outside");
    }

    public void Dispose()
    {
        Directory.Delete(_parent, recursive: true);
        GC.SuppressFinalize(this);
    }

    private LocalFileStorage Storage() => new(Path.Combine(_parent, "uploads"));

    [Fact]
    public void OpenRead_AFileInsideTheBase_Opens()
    {
        using var stream = Storage().OpenRead("global/inside.txt");

        new StreamReader(stream).ReadToEnd().Should().Be("inside");
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("global/../../outside.txt")]
    [InlineData("../uploads-old/secret.txt")]
    public void OpenRead_APathThatLeavesTheBase_IsRefused(string relativePath)
    {
        var open = () => Storage().OpenRead(relativePath);

        open.Should().Throw<InvalidOperationException>(
            "a path outside the base folder is refused before any file is touched, a sibling that shares the base's name prefix included");
    }

    [Fact]
    public void OpenRead_AnAbsolutePath_IsRefused()
    {
        var open = () => Storage().OpenRead(Path.Combine(_parent, "outside.txt"));

        open.Should().Throw<InvalidOperationException>("Path.Combine drops the base for a rooted path, so the check must catch it");
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    public void OpenRead_TheBaseFolderItself_IsRefused(string relativePath)
    {
        var open = () => Storage().OpenRead(relativePath);

        open.Should().Throw<InvalidOperationException>("the base folder is not a file under it");
    }

    [Fact]
    public void Delete_APathThatLeavesTheBase_IsRefused_AndTheFileSurvives()
    {
        var delete = () => Storage().Delete("../uploads-old/secret.txt");

        delete.Should().Throw<InvalidOperationException>();
        File.Exists(Path.Combine(_parent, "uploads-old", "secret.txt")).Should().BeTrue();
    }
}
