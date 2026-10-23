# syntax=docker/dockerfile:1
# Build from the repo ROOT: the API needs Croft.Shared and the root global.json. Cross-builds
# without emulation: the SDK stage runs on the build machine ($BUILDPLATFORM) and
# cross-publishes for $TARGETARCH, so both --platform linux/arm64 (the cluster) and
# --platform linux/amd64 (local checks) work.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY global.json Directory.Build.props .editorconfig ./
COPY src/Croft.Shared/ src/Croft.Shared/
COPY src/Croft.Api/ src/Croft.Api/

# WORKAROUND(W-088, .NET): precompiled (ReadyToRun) code, so a new pod does not spend minutes JIT-recompiling hot paths; the .NET 10 arm64 target is armv8.0-a
# ReadyToRun: crossgen2 (from the SDK's normal restore) precompiles the assemblies for
# $TARGETARCH, cross-compiling from the build platform. The .NET 10 linux-arm64 target is
# armv8.0-a, which the Raspberry Pi 4 (Cortex-A72) supports; see docs/workarounds.md W-088 for
# the .NET 11 check. tests/Croft.Api.Tests/ReadyToRunPublishTests.cs publishes with this very
# line, so keep it on ONE line starting "RUN dotnet publish".
RUN dotnet publish src/Croft.Api/Croft.Api.csproj -c Release -a "$TARGETARCH" --self-contained false -p:PublishReadyToRun=true -o /app/publish
# END WORKAROUND(W-088)

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
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
