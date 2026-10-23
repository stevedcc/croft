using System.Diagnostics;

namespace Croft.Shared.Telemetry;

/// <summary>
/// Marks an outbound HttpClient request whose span must not export its path (brief 12a): an avatar's address can carry a handle or a DID, and the privacy
/// rule is that no handle, name or text leaves the process in telemetry. A request carrying <see cref="HostOnly"/> is exported with its host alone.
/// </summary>
public static class TelemetryRedaction
{
    public static readonly HttpRequestOptionsKey<bool> HostOnly = new("croft.telemetry.host-only");

    private static readonly string[] UrlTags = ["url.full", "http.url"];

    private static readonly string[] PathTags = ["url.path", "http.target", "url.query"];

    /// <summary>Rewrites the URL tags of <paramref name="activity"/> to the host when <paramref name="request"/> asks for it; never throws.</summary>
    public static void Apply(Activity activity, HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Options.TryGetValue(HostOnly, out var hostOnly) || !hostOnly || request.RequestUri is not { IsAbsoluteUri: true } uri)
            return;

        var hostUrl = $"{uri.Scheme}://{uri.Authority}/";
        foreach (var tag in UrlTags)
        {
            if (activity.GetTagItem(tag) is not null)
                activity.SetTag(tag, hostUrl);
        }

        foreach (var tag in PathTags)
        {
            if (activity.GetTagItem(tag) is not null)
                activity.SetTag(tag, tag == "url.query" ? string.Empty : "/");
        }
    }
}
