# syntax=docker/dockerfile:1@sha256:4edf897a3ffa55b89f906fc8cc78afdb3f1834cc9c7083565e611a8a7d5fe99e
# Build from the repo ROOT: the API needs Croft.Shared and the root global.json. Cross-builds
# without emulation: the SDK stage runs on the build machine ($BUILDPLATFORM) and
# cross-publishes for $TARGETARCH, so both --platform linux/arm64 (the cluster) and
# --platform linux/amd64 (local checks) work.

# WORKAROUND(W-103, Testcontainers): the Engine API's legacy builder predefines no BUILDPLATFORM or TARGETARCH
# Global platform args. BuildKit and buildah fill them in automatically and keep doing so; the
# Docker Engine's legacy builder (which Testcontainers builds through) predefines neither, so
# `FROM --platform=$BUILDPLATFORM` would be empty there. Declared here, the legacy builder takes
# them from build arguments (the image tests pass both, see docs/workarounds.md W-103).
ARG BUILDPLATFORM
ARG TARGETARCH
# END WORKAROUND(W-103)

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0@sha256:3a9175b6c54b2f076817af1360f691c9994447b192faa48a1a1f112a516844c7 AS build
ARG TARGETARCH
# The released version, from the tag (release.yml passes VERSION=<tag without the v>). Empty
# (the default) keeps the <Version> in Directory.Build.props, as a local build does.
ARG VERSION=
WORKDIR /src

COPY global.json Directory.Build.props .editorconfig ./
COPY src/Croft.Shared/ src/Croft.Shared/
COPY src/Croft.Api/ src/Croft.Api/

# WORKAROUND(W-088, .NET): precompiled (ReadyToRun) code, so a new pod does not spend minutes JIT-recompiling hot paths; the arm64 instruction sets are pinned to the Pi 4's
# ReadyToRun: crossgen2 (from the SDK's normal restore) precompiles the assemblies for
# $TARGETARCH, cross-compiling from the build platform. .NET 11's linux-arm64 target needs LSE,
# which the Raspberry Pi 4 (Cortex-A72) lacks, so Directory.Build.props (copied above) pins the
# instruction sets for linux-arm64; see docs/workarounds.md W-088.
# tests/Croft.Api.Tests/ReadyToRunPublishTests.cs publishes with this very line, so keep it on
# ONE line starting "RUN dotnet publish".
RUN dotnet publish src/Croft.Api/Croft.Api.csproj -c Release -a "$TARGETARCH" --self-contained false -p:PublishReadyToRun=true -o /app/publish ${VERSION:+-p:Version=$VERSION}
# END WORKAROUND(W-088)

FROM mcr.microsoft.com/dotnet/aspnet:11.0@sha256:bae0269049d5061ab7f49995cc1acebf54b75471c096c4a1f874e27661d44045 AS final
WORKDIR /app

COPY --from=build /app/publish .

# WORKAROUND(W-082, .NET): runAsNonRoot needs a numeric user; the aspnet image listens on 8080 by default
# The image's non-root user, by number (APP_UID=1654 in the .NET images): Kubernetes'
# runAsNonRoot can only verify a numeric user and refuses to start a container whose
# image names one (`USER app`). fsGroup in the deployment matches it.
USER $APP_UID
# The .NET 8+ aspnet images listen on 8080 by default (ASPNETCORE_HTTP_PORTS).
EXPOSE 8080
# END WORKAROUND(W-082)

# The k8s init container runs this entrypoint with the "migrate" argument (see k8s/): it
# applies the pending migrations under an advisory lock and exits.
ENTRYPOINT ["dotnet", "Croft.Api.dll"]
