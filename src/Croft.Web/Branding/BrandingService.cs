using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;

namespace Croft.Web;

/// <summary>
/// Builds the site's <see cref="Branding"/> once at startup from the branding folder
/// (<c>Blog:BrandingPath</c>, default <c>/app/branding</c>): <c>branding.json</c> merges over the
/// built-in defaults field by field and token by token. A folder or file that is missing means
/// the defaults with one Information line; malformed JSON, an invalid colour or an unknown token
/// fails startup with a message naming the field. Below-AA contrast pairs log one Warning each.
/// </summary>
public sealed partial class BrandingService(Branding branding, IReadOnlyDictionary<string, string>? filePaths = null)
{
    /// <summary>Physical file per served name (site file if present, else the default), when it exists.</summary>
    private readonly IReadOnlyDictionary<string, string> _filePaths = filePaths ?? FrozenDictionary<string, string>.Empty;

    /// <summary>The image file names a site's folder may supply and the endpoint serves.</summary>
    private static readonly string[] ImageFiles =
        ["logo.svg", "favicon.ico", "apple-touch-icon.png", "fallback-banner.svg", "fallback-banner.png", "fallback-banner.jpg",
         "masthead-banner.svg", "masthead-banner.png", "masthead-banner.jpg"];

    private static readonly string[] MastheadBannerFiles = ["masthead-banner.svg", "masthead-banner.png", "masthead-banner.jpg"];

    private static readonly string[] FallbackBannerFiles = ["fallback-banner.svg", "fallback-banner.png", "fallback-banner.jpg"];

    private const string DefaultFallbackBanner = "fallback-banner.svg";

    public Branding Current { get; } = branding;

    /// <summary>The generated theme.css: the colour blocks, built once at startup.</summary>
    public string ThemeCss { get; } = GenerateThemeCss(branding);

    /// <summary>The generated manifest.webmanifest body, built once at startup.</summary>
    public string ManifestJson { get; } = GenerateManifestJson(branding);

    /// <summary>Reads the branding folder and builds the branding (and its content hashes) once.</summary>
    /// <param name="folderPath">The site's folder; empty or missing means the built-in defaults.</param>
    /// <param name="defaultFilesPath">Where the framework's default images live (wwwroot/branding-default).</param>
    /// <param name="logger">Where the Information/Warning lines go; null logs nothing.</param>
    public static BrandingService Load(string? folderPath, string? defaultFilesPath = null, ILogger? logger = null)
    {
        var merged = MergeJson(folderPath, logger);
        var (withFiles, paths) = ResolveImageFiles(merged, folderPath, defaultFilesPath);
        WarnBelowAA(withFiles, logger);
        return new(withFiles, paths);
    }

    /// <summary>The file behind a /branding/{file} request: one of the served names — for the
    /// fallback and masthead banners the resolved file, whatever extension the request used (a
    /// masthead banner the site did not supply is 404) — or null (404) for any other name.</summary>
    public string? ResolveFile(string requestName)
    {
        var servedName = requestName switch
        {
            "logo.svg" or "favicon.ico" or "apple-touch-icon.png" => requestName,
            "fallback-banner.svg" or "fallback-banner.png" or "fallback-banner.jpg" => Current.FallbackBannerFile,
            "masthead-banner.svg" or "masthead-banner.png" or "masthead-banner.jpg" => Current.MastheadBannerFile,
            _ => null,
        };
        return servedName is null ? null : _filePaths.GetValueOrDefault(servedName);
    }

    private static Branding MergeJson(string? folderPath, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            if (logger is not null)
            {
                LogNoFolderConfigured(logger);
            }

            return Branding.Defaults;
        }

        var jsonPath = Path.Combine(folderPath, "branding.json");
        if (!Directory.Exists(folderPath) || !File.Exists(jsonPath))
        {
            if (logger is not null)
            {
                LogNoFolderAt(logger, folderPath);
            }

            return Branding.Defaults;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        }
        catch (JsonException ex)
        {
            throw new BrandingException($"branding.json: {ex.Message}");
        }

        using (document)
        {
            return Merge(Branding.Defaults, document.RootElement);
        }
    }

    private static Branding Merge(Branding defaults, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new BrandingException("branding.json: the document must be a JSON object");
        }

        var name = defaults.Name;
        var tagline = defaults.Tagline;
        var description = defaults.Description;
        var author = defaults.Author;
        var masthead = defaults.Masthead;
        var showPoweredBy = defaults.ShowPoweredBy;
        var poweredByUrl = defaults.PoweredByUrl;
        var profiles = defaults.Profiles;
        var fediverseCreator = defaults.FediverseCreator;
        var light = defaults.Light;
        var dark = defaults.Dark;

        foreach (var field in root.EnumerateObject())
        {
            switch (field.Name)
            {
                case "name":
                    name = TextField(field);
                    break;
                case "tagline":
                    tagline = TextField(field);
                    break;
                case "description":
                    description = TextField(field);
                    break;
                case "author":
                    author = TextField(field);
                    break;
                case "masthead":
                    masthead = field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? field.Value.GetBoolean()
                        : throw new BrandingException("branding.json: masthead must be a boolean (true or false)");
                    break;
                case "showPoweredBy":
                    showPoweredBy = field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? field.Value.GetBoolean()
                        : throw new BrandingException("branding.json: showPoweredBy must be a boolean (true or false)");
                    break;
                case "poweredByUrl":
                    poweredByUrl = HttpUrlField(field);
                    break;
                case "profiles":
                    profiles = ProfilesField(field);
                    break;
                case "fediverseCreator":
                    fediverseCreator = FediverseCreatorField(field);
                    break;
                case "palette":
                    (light, dark) = PaletteField(field, light, dark);
                    break;
                default:
                    throw new BrandingException($"branding.json: {field.Name} is not a known field");
            }
        }

        return defaults with
        {
            Name = name,
            Tagline = tagline,
            Description = description,
            Author = author,
            Masthead = masthead,
            ShowPoweredBy = showPoweredBy,
            PoweredByUrl = poweredByUrl,
            Profiles = profiles,
            FediverseCreator = fediverseCreator,
            Light = light,
            Dark = dark,
        };
    }

    private static string TextField(JsonProperty field) =>
        field.Value.ValueKind == JsonValueKind.String
            ? field.Value.GetString()!
            : throw new BrandingException($"branding.json: {field.Name} must be a string");

    /// <summary>A string that is empty or an absolute http(s) URL: it becomes a link's href, so
    /// no other scheme (javascript:, data:) may pass.</summary>
    private static string HttpUrlField(JsonProperty field)
    {
        var value = TextField(field);
        return value.Length == 0
            || (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            ? value
            : throw new BrandingException($"branding.json: {field.Name} must be an absolute http or https URL");
    }

    /// <summary>The owner's profiles (brief 11a item 4): an array of <c>{"service", "url"}</c>
    /// objects, kept in the owner's order — that order renders everywhere (head links, the
    /// JSON-LD sameAs array, the footer icons). The service must be one of
    /// <see cref="BrandProfile.KnownServices"/>; the URL must be an absolute https one (like
    /// <see cref="HttpUrlField"/> but https-only: it becomes a <c>rel="me"</c> href on the
    /// site's origin and must never downgrade).</summary>
    private static IReadOnlyList<BrandProfile> ProfilesField(JsonProperty field)
    {
        if (field.Value.ValueKind != JsonValueKind.Array)
        {
            throw new BrandingException("branding.json: profiles must be an array of { service, url } objects");
        }

        List<BrandProfile> profiles = [];
        var index = 0;
        foreach (var entry in field.Value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new BrandingException($"branding.json: profiles[{index}] must be an object with service and url");
            }

            string? service = null;
            string? url = null;
            foreach (var property in entry.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "service":
                        service = TextField(property);
                        break;
                    case "url":
                        url = HttpsUrlField(property, $"profiles[{index}].url");
                        break;
                    default:
                        throw new BrandingException(
                            $"branding.json: profiles[{index}].{property.Name} is not a known field");
                }
            }

            if (service is null || url is null)
            {
                throw new BrandingException($"branding.json: profiles[{index}] must carry both service and url");
            }

            if (!BrandProfile.KnownServices.Contains(service))
            {
                throw new BrandingException(
                    $"branding.json: profiles[{index}].service '{service}' is not a known service "
                    + $"({string.Join(", ", BrandProfile.KnownServices)})");
            }

            profiles.Add(new BrandProfile(service, url));
            index++;
        }

        return profiles;
    }

    /// <summary>A profile's URL: an absolute https URL, validated exactly like
    /// <see cref="HttpUrlField"/> but https-only (brief 11a item 4). The label names the field
    /// as the site wrote it (e.g. <c>profiles[0].url</c>).</summary>
    private static string HttpsUrlField(JsonProperty field, string label)
    {
        var value = TextField(field);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https"
            ? value
            : throw new BrandingException($"branding.json: {label} must be an absolute https URL");
    }

    /// <summary>The owner's fediverse handle (brief 11a item 4): <c>@user@host</c>, or empty
    /// (unset, no meta renders). Anything else fails startup.</summary>
    private static string FediverseCreatorField(JsonProperty field)
    {
        var value = TextField(field);
        return value.Length == 0 || IsFediverseHandle(value)
            ? value
            : throw new BrandingException("branding.json: fediverseCreator must look like @user@host");
    }

    /// <summary><c>@user@host</c>: a leading <c>@</c>, a non-empty local part, exactly one more
    /// <c>@</c>, and a non-empty host.</summary>
    private static bool IsFediverseHandle(string value)
    {
        if (!value.StartsWith('@'))
        {
            return false;
        }

        var hostAt = value.IndexOf('@', 1);
        return hostAt > 1 && hostAt == value.LastIndexOf('@') && hostAt < value.Length - 1;
    }

    private static (BrandPalette Light, BrandPalette Dark) PaletteField(
        JsonProperty field, BrandPalette light, BrandPalette dark)
    {
        if (field.Value.ValueKind != JsonValueKind.Object)
        {
            throw new BrandingException("branding.json: palette must be an object");
        }

        foreach (var mode in field.Value.EnumerateObject())
        {
            var isLight = mode.Name switch
            {
                "light" => true,
                "dark" => false,
                _ => throw new BrandingException($"branding.json: palette.{mode.Name} is not a known palette mode"),
            };

            if (mode.Value.ValueKind != JsonValueKind.Object)
            {
                throw new BrandingException($"branding.json: palette.{mode.Name} must be an object");
            }

            var palette = isLight ? light : dark;
            foreach (var token in mode.Value.EnumerateObject())
            {
                if (!PaletteTokens.All.Contains(token.Name))
                {
                    throw new BrandingException(
                        $"branding.json: palette.{mode.Name}.{token.Name} is not a known palette token");
                }

                if (token.Value.ValueKind != JsonValueKind.String)
                {
                    throw new BrandingException(
                        $"branding.json: palette.{mode.Name}.{token.Name} must be a string");
                }

                var value = token.Value.GetString()!;
                if (!BrandingColor.TryParse(value, out _, out _, out _, out _))
                {
                    throw new BrandingException(
                        $"branding.json: palette.{mode.Name}.{token.Name} \"{value}\" is not a colour");
                }

                palette = palette.With(token.Name, value);
            }

            if (isLight)
            {
                light = palette;
            }
            else
            {
                dark = palette;
            }
        }

        return (light, dark);
    }

    /// <summary>Records which image files the site supplied, hashes every served file —
    /// the site's file if present, else the framework default — and keeps its path for serving.</summary>
    private static (Branding Branding, IReadOnlyDictionary<string, string> Paths) ResolveImageFiles(
        Branding branding, string? folderPath, string? defaultFilesPath)
    {
        var supplied = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath))
        {
            foreach (var file in ImageFiles)
            {
                if (File.Exists(Path.Combine(folderPath, file)))
                {
                    supplied.Add(file);
                }
            }
        }

        var fallback = FallbackBannerFiles.FirstOrDefault(supplied.Contains) ?? DefaultFallbackBanner;

        // Unlike the fallback banner there is no framework default masthead file: with none
        // supplied the masthead uses the fallback banner (theme.css's var() fallback).
        var mastheadBanner = MastheadBannerFiles.FirstOrDefault(supplied.Contains);

        Dictionary<string, string> hashes = [with(StringComparer.Ordinal)];
        Dictionary<string, string> paths = [with(StringComparer.Ordinal)];
        foreach (var servedName in new[] { "logo.svg", "favicon.ico", "apple-touch-icon.png", fallback, mastheadBanner }
            .OfType<string>())
        {
            if (ResolvedFile(servedName, supplied, folderPath, defaultFilesPath) is { } path)
            {
                paths[servedName] = path;
                hashes[servedName] = Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(path)))[..16].ToLowerInvariant();
            }
        }

        return (branding with
        {
            SuppliedFiles = supplied.ToFrozenSet(),
            FileHashes = hashes.ToFrozenDictionary(),
            FallbackBannerFile = fallback,
            MastheadBannerFile = mastheadBanner,
        }, paths.ToFrozenDictionary());
    }

    /// <summary>The file the endpoint serves for one name: the site's, else the default's.
    /// The fallback banner's served name is already resolved (first of svg, png, jpg the site
    /// supplies, else the default svg).</summary>
    private static string? ResolvedFile(
        string servedName, IReadOnlySet<string> supplied, string? folderPath, string? defaultFilesPath)
    {
        if (supplied.Contains(servedName) && folderPath is not null)
        {
            return Path.Combine(folderPath, servedName);
        }

        var fallbackPath = defaultFilesPath is null ? null : Path.Combine(defaultFilesPath, servedName);
        return fallbackPath is not null && File.Exists(fallbackPath) ? fallbackPath : null;
    }

    private static void WarnBelowAA(Branding branding, ILogger? logger)
    {
        foreach (var (mode, palette) in new[] { ("light", branding.Light), ("dark", branding.Dark) })
        {
            foreach (var (pairs, required) in new[] { (PaletteTokens.ContrastPairs, 4.5), (PaletteTokens.NonTextContrastPairs, 3.0) })
            {
                foreach (var (foreground, background) in pairs)
                {
                    var foregroundValue = PaletteTokens.ValueOf(palette, foreground);
                    var backgroundValue = PaletteTokens.ValueOf(palette, background);
                    // A translucent colour's contrast depends on what is behind it: skip the pair.
                    if (BrandingColor.IsTranslucent(foregroundValue) || BrandingColor.IsTranslucent(backgroundValue))
                    {
                        continue;
                    }

                    var ratio = BrandingContrast.Ratio(foregroundValue, backgroundValue);
                    if (ratio < required && logger is not null)
                    {
                        LogLowContrast(logger, mode, foreground, background,
                            ratio.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                            required.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No branding folder configured; using the built-in defaults.")]
    private static partial void LogNoFolderConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "No branding folder at {FolderPath}; using the built-in defaults.")]
    private static partial void LogNoFolderAt(ILogger logger, string folderPath);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "branding.json: palette.{Mode} {Foreground}/{Background} contrast is {Ratio}:1, below {Required}:1")]
    private static partial void LogLowContrast(ILogger logger, string mode, string foreground, string background, string ratio, string required);

    /// <summary>theme.css: the light values on :root, the dark values for the explicit
    /// data-theme attribute and for the system colour scheme — the blocks app.css held,
    /// generated from the site's palette so the colours are defined once. The fallback
    /// banner's URL joins :root as its own token: it carries the file's content hash and
    /// changes with the site's file but not with the colour mode, and as a token it paints
    /// the fallback surfaces without any inline style attribute. The masthead banner, when the
    /// site supplies one, is a second token of the same kind (<c>--masthead-banner</c>); the
    /// masthead's CSS falls back to <c>--fallback-banner</c> when it is not defined.</summary>
    private static string GenerateThemeCss(Branding branding) =>
        string.Join('\n',
        [
            "/* Generated once at startup from the site's branding folder (Blog:BrandingPath). */",
            ":root {",
            TokenBlock(branding.Light, "    "),
            $"    --fallback-banner: url('{branding.FileUrl(branding.FallbackBannerFile)}');",
            .. branding.MastheadBannerFile is { } masthead
                ? (string[])[$"    --masthead-banner: url('{branding.FileUrl(masthead)}');"]
                : [],
            "}",
            "",
            ":root[data-theme=dark] {",
            TokenBlock(branding.Dark, "    "),
            "}",
            "",
            "@media (prefers-color-scheme: dark) {",
            "    :root:not([data-theme=light]) {",
            TokenBlock(branding.Dark, "        "),
            "    }",
            "}",
            "",
        ]);

    private static string TokenBlock(BrandPalette palette, string indent) =>
        string.Join('\n', PaletteTokens.All.Select(token => $"{indent}--{token}: {palette[token]};"));

    /// <summary>manifest.webmanifest: named for the site, coloured from the light palette,
    /// standalone, with the icons from the branding endpoint.</summary>
    private static string GenerateManifestJson(Branding branding) =>
        JsonSerializer.Serialize(new
        {
            name = branding.Name,
            short_name = branding.Name,
            start_url = "/",
            display = "standalone",
            background_color = branding.Light[PaletteTokens.Bg],
            theme_color = branding.Light[PaletteTokens.Primary],
            icons = new object[]
            {
                new { src = branding.FileUrl("logo.svg"), sizes = "any", type = "image/svg+xml" },
                new { src = branding.FileUrl("apple-touch-icon.png"), sizes = "180x180", type = "image/png" },
            },
        }, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
