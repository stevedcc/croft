using System.Collections.Frozen;

namespace Croft.Web;

/// <summary>
/// The site's identity, immutable, built once at startup from the branding folder (or the
/// built-in defaults): the words, the two palettes, which image files the site supplied, and a
/// content hash per served file (the <c>?v=</c> cache-busters).
/// </summary>
public sealed record Branding
{
    public required string Name { get; init; }

    public string Tagline { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Author { get; init; } = string.Empty;

    public BrandPalette Light { get; init; } = BrandPalette.DefaultLight;

    public BrandPalette Dark { get; init; } = BrandPalette.DefaultDark;

    /// <summary>The image file names the site's own folder supplied.</summary>
    public IReadOnlySet<string> SuppliedFiles { get; init; } = FrozenSet<string>.Empty;

    /// <summary>Content hash per served file (its served name, e.g. "fallback-banner.png").</summary>
    public IReadOnlyDictionary<string, string> FileHashes { get; init; } = FrozenDictionary<string, string>.Empty;

    /// <summary>The served name of the fallback banner: the first of svg, png, jpg the site
    /// supplies, else the default svg.</summary>
    public string FallbackBannerFile { get; init; } = "fallback-banner.svg";

    /// <summary>Whether the home page's first page carries the site masthead (branding.json
    /// <c>masthead</c>, default true).</summary>
    public bool Masthead { get; init; } = true;

    /// <summary>Whether the footer carries "Running on Croft vX.Y.Z" (branding.json
    /// <c>showPoweredBy</c>, default true).</summary>
    public bool ShowPoweredBy { get; init; } = true;

    /// <summary>The absolute http(s) URL "Croft" links to in the footer (branding.json
    /// <c>poweredByUrl</c>); empty means plain text.</summary>
    public string PoweredByUrl { get; init; } = string.Empty;

    /// <summary>The owner's profile links (branding.json <c>profiles</c>), in the owner's
    /// order: rendered as <c>rel="me"</c> head links and footer icons and listed as the JSON-LD
    /// author's <c>sameAs</c>. Empty means none.</summary>
    public IReadOnlyList<BrandProfile> Profiles { get; init; } = [];

    /// <summary>The owner's fediverse handle (branding.json <c>fediverseCreator</c>,
    /// <c>@user@host</c>); empty means no <c>fediverse:creator</c> meta.</summary>
    public string FediverseCreator { get; init; } = string.Empty;

    /// <summary>The served name of the masthead's own banner: the first of masthead-banner.svg,
    /// .png, .jpg the site supplies, or null when it supplies none (the masthead then uses the
    /// fallback banner). Served and hashed exactly like <see cref="FallbackBannerFile"/>.</summary>
    public string? MastheadBannerFile { get; init; }

    /// <summary>The URL a served image is linked by, with its content-hash cache-buster.</summary>
    public string FileUrl(string servedName) =>
        $"/branding/{servedName}?v={FileHashes.GetValueOrDefault(servedName, string.Empty)}";

    /// <summary>The built-in defaults: "My Blog", empty words, the neutral slate-and-blue palettes.</summary>
    public static Branding Defaults { get; } = new() { Name = "My Blog" };
}
