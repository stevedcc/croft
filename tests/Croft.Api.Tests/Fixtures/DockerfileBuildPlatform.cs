// WORKAROUND-FILE(W-103, Testcontainers): the base-image pre-pull reads FROM --platform=$BUILDPLATFORM literally, and the legacy builder predefines no platform args
using System.Runtime.InteropServices;
using DotNet.Testcontainers.Builders;

namespace Croft.Api.Tests.Fixtures;

/// <summary>Testcontainers pulls a Dockerfile's base images itself before the build, and for a
/// digest-pinned <c>FROM --platform=$BUILDPLATFORM image@sha256:…</c> it asks for the OS
/// "$BUILDPLATFORM" unless the argument is passed explicitly. It then builds through the Docker
/// Engine API's legacy builder, which (unlike BuildKit and buildah) predefines neither
/// BUILDPLATFORM nor TARGETARCH. The build runs on this host, so both are the host's platform,
/// as BuildKit would set them.</summary>
internal static class DockerfileBuildPlatform
{
    extension(ImageFromDockerfileBuilder builder)
    {
        public ImageFromDockerfileBuilder WithHostBuildPlatform() =>
            builder
                .WithBuildArgument("BUILDPLATFORM", $"linux/{HostArchitecture()}")
                .WithBuildArgument("TARGETARCH", HostArchitecture());
    }

    private static string HostArchitecture() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        var other => throw new PlatformNotSupportedException($"No Linux build platform for {other}."),
    };
}
