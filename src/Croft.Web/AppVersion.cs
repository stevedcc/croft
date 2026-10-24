using System.Reflection;

namespace Croft.Web;

/// <summary>The released version the footer shows ("Running on Croft v0.6.1"): the release tag
/// (the Dockerfiles publish with <c>-p:Version</c>), or the <c>&lt;Version&gt;</c> in
/// Directory.Build.props for a local build, read from the assembly's informational version
/// without the <c>+commit</c> build metadata the SDK appends.</summary>
public static class AppVersion
{
    public static string Current { get; } = WithoutBuildMetadata(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3)
        ?? string.Empty);

    /// <summary>The version with any <c>+metadata</c> suffix removed.</summary>
    public static string WithoutBuildMetadata(string informationalVersion)
    {
        ArgumentNullException.ThrowIfNull(informationalVersion);
        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informationalVersion : informationalVersion[..plus];
    }
}
