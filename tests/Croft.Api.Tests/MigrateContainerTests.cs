using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using AwesomeAssertions;

namespace Croft.Api.Tests;

/// <summary>
/// The shipped images, run as Kubernetes runs the init containers: <c>migrate</c> against a
/// database that refuses connections. Production (2026-10-17) logged an unhandled exception and
/// then never exited, so the pods stayed in Init:0/1 for a day. The entry must end with exit
/// code 1 (not 134/139 from a crash, not a hang) so that Kubernetes restarts the init container.
/// These build the Dockerfiles, so they are slow: run them alone with
/// <c>--filter "Category=Container"</c>, skip them with <c>--filter "Category!=Container"</c>.
/// </summary>
[Trait("Category", "Container")]
public class MigrateContainerTests
{
    private static readonly TimeSpan ExitDeadline = TimeSpan.FromSeconds(30);

    [Fact]
    public Task ApiMigrate_AgainstARefusingDatabase_ExitsWithCodeOneWithin30Seconds() =>
        AssertMigrateFailsCleanlyAsync(
            "src/Croft.Api/Dockerfile", "ConnectionStrings__BlogDb", "Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p");

    [Fact]
    public Task WebMigrate_AgainstARefusingDatabase_ExitsWithCodeOneWithin30Seconds() =>
        AssertMigrateFailsCleanlyAsync(
            "src/Croft.Web/Dockerfile", "ConnectionStrings__WebDb", "Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p");

    private static async Task AssertMigrateFailsCleanlyAsync(string dockerfile, string connectionStringName, string connectionString)
    {
        var image = new ImageFromDockerfileBuilder()
            .WithName($"croft-migrate-test/{Path.GetFileName(Path.GetDirectoryName(dockerfile))!.ToLowerInvariant()}:{Guid.NewGuid():N}")
            .WithDockerfileDirectory(RepoRoot())
            .WithDockerfile(dockerfile)
            .WithDeleteIfExists(true)
            .Build();
        await image.CreateAsync();
        try
        {
            await AssertExitsWithCodeOneAsync(image, connectionStringName, connectionString);
        }
        finally
        {
            // Disposing a container never deletes the image it ran; delete it, or every run
            // leaves two behind. AssertExitsWithCodeOneAsync has disposed its container by now.
            await image.DeleteAsync();
        }
    }

    private static async Task AssertExitsWithCodeOneAsync(IImage image, string connectionStringName, string connectionString)
    {
        await using var container = new ContainerBuilder(image)
            .WithEnvironment(connectionStringName, connectionString)
            .WithEnvironment("Database__MigrateTimeout", "00:00:10")
            .WithCommand("migrate")
            .WithWaitStrategy(Wait.ForUnixContainer().AddCustomWaitStrategy(new ContainerHasExited(), o => o.WithTimeout(ExitDeadline)))
            .Build();

        // The wait strategy fails the start with a timeout when the container is still running
        // after the deadline (the production symptom: a process that never exits). A container
        // that has already exited makes Testcontainers throw ContainerNotRunningException; that is
        // the expected path here, and the exit code is asserted below.
        try
        {
            await container.StartAsync();
        }
        catch (ContainerNotRunningException)
        {
            container.State.Should().Be(TestcontainersStates.Exited);
        }

        var exitCode = await container.GetExitCodeAsync();
        exitCode.Should().Be(1, "migrate must log the failure and exit with 1, not crash (134/139) or hang");
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
        public Task<bool> UntilAsync(IContainer container) =>
            Task.FromResult(container.State == TestcontainersStates.Exited);
    }
}
