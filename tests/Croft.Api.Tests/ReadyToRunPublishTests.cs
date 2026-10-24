using System.Diagnostics;
using System.Reflection.PortableExecutable;
using AwesomeAssertions;

namespace Croft.Api.Tests;

/// <summary>
/// The images ship ReadyToRun code (a new pod otherwise burns 1.2-1.5 cores for about five
/// minutes after taking load, while the tiered JIT recompiles the hot paths). This publishes
/// each app with the exact <c>dotnet publish</c> line of its Dockerfile for linux-arm64 and
/// opens the published assemblies: each must carry a ReadyToRun header and target arm64.
/// Reading the flags from the Dockerfile means the test fails when the Dockerfile drops the
/// flag. Publishing is slow, so run it alone with <c>--filter "Category=ReadyToRun"</c> or skip
/// it with <c>--filter "Category!=ReadyToRun"</c>.
/// </summary>
[Trait("Category", "ReadyToRun")]
public class ReadyToRunPublishTests
{
    // ReadyToRun images carry the target OS in the PE machine field: IMAGE_FILE_MACHINE_ARM64
    // (0xAA64) XOR the Linux marker (0x7B79).
    private const ushort Arm64MachineForLinuxReadyToRun = 0xAA64 ^ 0x7B79;

    [Theory]
    [InlineData("src/Croft.Api/Dockerfile", "Croft.Api.dll")]
    [InlineData("src/Croft.Web/Dockerfile", "Croft.Web.dll")]
    public void PublishedWithTheDockerfilesFlags_TheAppAndSharedAssembliesAreReadyToRunForArm64(
        string dockerfile, string appAssembly)
    {
        var root = RepoRoot();
        var output = Path.Combine(Path.GetTempPath(), "croft-r2r-" + Guid.NewGuid().ToString("N"));
        try
        {
            Publish(root, dockerfile, output);

            // Brief 16a: the SDK 11 parses the value of --self-contained (the migration notes say
            // earlier SDKs read the bare flag as true); the image runs on the aspnet runtime image,
            // so the publish must stay framework-dependent: no runtime of its own, and a
            // runtimeconfig that names the shared framework.
            File.Exists(Path.Combine(output, "libcoreclr.so")).Should()
                .BeFalse("the image's runtime comes from the aspnet base image (--self-contained false)");
            File.ReadAllText(Path.Combine(output, Path.ChangeExtension(appAssembly, ".runtimeconfig.json"))).Should()
                .Contain("\"Microsoft.AspNetCore.App\"", "a framework-dependent app names the shared framework it runs on");

            if (appAssembly == "Croft.Api.dll")
            {
                // Brief 12a: the avatar library's native part must travel with the API image, for the cluster's CPU.
                // A runtime-specific publish puts the native library next to the app; it must be the ELF build for arm64 (e_machine 183).
                var skia = Path.Combine(output, "libSkiaSharp.so");
                File.Exists(skia).Should().BeTrue("the API decodes avatars with SkiaSharp, whose native library the linux-arm64 image needs");
                var header = File.ReadAllBytes(skia).AsSpan(0, 20);
                header[..4].ToArray().Should().Equal(0x7F, (byte)'E', (byte)'L', (byte)'F');
                BitConverter.ToUInt16(header[18..20]).Should().Be(183, "libSkiaSharp.so is built for aarch64, the cluster's CPU");
            }

            foreach (var assembly in new[] { appAssembly, "Croft.Shared.dll" })
            {
                var path = Path.Combine(output, assembly);
                File.Exists(path).Should().BeTrue($"{assembly} is part of the published output");
                using var stream = File.OpenRead(path);
                using var reader = new PEReader(stream);
                reader.PEHeaders.CorHeader.Should().NotBeNull();
                reader.PEHeaders.CorHeader!.ManagedNativeHeaderDirectory.Size.Should()
                    .BeGreaterThan(0, $"{assembly} must carry a ReadyToRun header (precompiled code)");
                ((ushort)reader.PEHeaders.CoffHeader.Machine).Should()
                    .Be(Arm64MachineForLinuxReadyToRun, $"{assembly} must be compiled for linux-arm64");
            }
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    /// <summary>Runs the Dockerfile's own <c>dotnet publish</c> line with TARGETARCH=arm64 and our output folder.</summary>
    private static void Publish(string root, string dockerfile, string output)
    {
        var line = File.ReadAllLines(Path.Combine(root, dockerfile))
            .Single(l => l.StartsWith("RUN dotnet publish ", StringComparison.Ordinal));
        var arguments = line["RUN ".Length..]["dotnet ".Length..]
            .Replace("\"$TARGETARCH\"", "arm64", StringComparison.Ordinal)
            .Replace("/app/publish", $"\"{output}\"", StringComparison.Ordinal)
            // VERSION is empty unless release.yml passes one: the expansion is then nothing.
            .Replace(" ${VERSION:+-p:Version=$VERSION}", "", StringComparison.Ordinal);
        arguments.Should().NotContain("$", "every Dockerfile variable is substituted");

        var start = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, $"dotnet publish {arguments} must succeed:\n{stdout.Result}\n{stderr.Result}");
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
}
