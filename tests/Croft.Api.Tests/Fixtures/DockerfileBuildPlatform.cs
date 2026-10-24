// WORKAROUND-FILE(W-103, Testcontainers): the base-image pre-pull reads FROM --platform=$BUILDPLATFORM image@digest literally
using System.Runtime.InteropServices;
using DotNet.Testcontainers.Builders;

namespace Croft.Api.Tests.Fixtures;

/// <summary>Testcontainers pulls a Dockerfile's base images itself before the build, and for a
/// digest-pinned <c>FROM --platform=$BUILDPLATFORM image@sha256:…</c> it asks for the OS
/// "$BUILDPLATFORM" unless the argument is passed explicitly. The build runs on this host, so
/// BUILDPLATFORM is the host's platform, as BuildKit would set it.</summary>
internal static class DockerfileBuildPlatform
{
    extension(ImageFromDockerfileBuilder builder)
    {
        public ImageFromDockerfileBuilder WithHostBuildPlatform() =>
            builder.WithBuildArgument("BUILDPLATFORM", HostPlatform());
    }

    private static string HostPlatform() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "linux/amd64",
        Architecture.Arm64 => "linux/arm64",
        var other => throw new PlatformNotSupportedException($"No Linux build platform for {other}."),
    };
}
