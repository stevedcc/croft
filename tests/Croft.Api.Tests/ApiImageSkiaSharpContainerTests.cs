using AwesomeAssertions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 12a: the API image decodes avatars with SkiaSharp, whose native library must find everything it needs in the runtime image (the
/// aspnet base has no fontconfig or similar). The shipped image is built from its Dockerfile and asked which libraries the native
/// library cannot find: <c>ldd</c> must name none.
/// </summary>
[Trait("Category", "Container")]
public class ApiImageSkiaSharpContainerTests
{
    [Fact]
    public async Task TheApiImage_CarriesTheNativeSkiaLibrary_AndTheImageHasEveryLibraryItNeeds()
    {
        var image = new ImageFromDockerfileBuilder()
            .WithName($"croft-skia-test/api:{Guid.NewGuid():N}")
            .WithDockerfileDirectory(RepoRoot())
            .WithDockerfile("src/Croft.Api/Dockerfile")
            .WithDeleteIfExists(true)
            .Build();
        await image.CreateAsync();
        try
        {
            await using var container = new ContainerBuilder(image)
                .WithEntrypoint("ldd", "/app/libSkiaSharp.so")
                .WithWaitStrategy(Wait.ForUnixContainer().AddCustomWaitStrategy(new ContainerHasExited(), o => o.WithTimeout(TimeSpan.FromSeconds(30))))
                .Build();
            try
            {
                await container.StartAsync();
            }
            catch (ContainerNotRunningException)
            {
                container.State.Should().Be(TestcontainersStates.Exited);
            }

            var (stdout, stderr) = await container.GetLogsAsync();
            (await container.GetExitCodeAsync()).Should().Be(0, $"ldd must find libSkiaSharp.so in /app:\n{stdout}\n{stderr}");
            stdout.Should().Contain("libc.so.6").And.NotContain("not found", "every library the native part needs is in the image");
        }
        finally
        {
            await image.DeleteAsync();
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root (global.json) not found.");
    }

    private sealed class ContainerHasExited : IWaitUntil
    {
        public Task<bool> UntilAsync(IContainer container) => Task.FromResult(container.State == TestcontainersStates.Exited);
    }
}
