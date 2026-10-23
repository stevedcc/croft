namespace Croft.Web;

/// <summary>One owner profile in <c>branding.json</c> (brief 11a item 4): the service name and
/// the profile's absolute https URL. The owner's order is kept everywhere (head links, the
/// JSON-LD sameAs array, the footer icons).</summary>
/// <param name="Service">One of <see cref="KnownServices"/>.</param>
/// <param name="Url">The absolute https URL of the profile.</param>
public sealed record BrandProfile(string Service, string Url)
{
    /// <summary>The services whose marks this project may render. Anything else fails startup
    /// (brief 11a item 4).</summary>
    internal static readonly IReadOnlySet<string> KnownServices =
        new HashSet<string>(System.StringComparer.Ordinal)
        {
            "mastodon", "bluesky", "github", "linkedin",
        };

    /// <summary>The service as people know it, for the icon link's <c>aria-label</c> and
    /// <c>title</c> ("Mastodon", "Bluesky", "GitHub", "LinkedIn").</summary>
    public string DisplayName => Service switch
    {
        "mastodon" => "Mastodon",
        "bluesky" => "Bluesky",
        "github" => "GitHub",
        "linkedin" => "LinkedIn",
        _ => Service,
    };
}
