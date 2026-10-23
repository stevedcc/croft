using System.Reflection;
using System.Text.RegularExpressions;
using Croft.Shared;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests;

/// <summary>
/// A page lives at /<c>slug</c>, so every first path segment the Web app serves for itself must
/// be a reserved slug — otherwise a page could silently shadow a route or an asset. Both lists
/// are derived mechanically here: the components' <see cref="RouteAttribute"/> templates and the
/// bootstrapped app's <see cref="EndpointDataSource"/>. A route or segment with no literal first
/// segment (/, /{slug}, /{**path}) constrains nothing and contributes nothing.
/// </summary>
public partial class ReservedSlugsGuardTests : IDisposable
{
    private readonly string _brandingFolder = CreateBrandingFolder();

    public void Dispose()
    {
        Directory.Delete(_brandingFolder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string CreateBrandingFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blog-reserved-slug-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "branding.json"), """{ "name": "Example Blog" }""");
        return path;
    }

    [Fact]
    public void EveryPageRoutesFirstLiteralSegment_IsAReservedSlug()
    {
        var segments = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(IComponent).IsAssignableFrom(t))
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>())
            .Select(a => FirstLiteralSegment(a.Template))
            .OfType<string>();

        AssertAllReserved(segments, "a routable component's route");
    }

    [Fact]
    public void EveryMappedEndpointsFirstLiteralSegment_IsAReservedSlug()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("Blog:BaseUrl", "https://example.com/");
            builder.UseSetting("Blog:BrandingPath", _brandingFolder);
        });

        var segments = factory.Services.GetRequiredService<IEnumerable<EndpointDataSource>>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => FirstLiteralSegment(e.RoutePattern.RawText))
            .OfType<string>();

        AssertAllReserved(segments, "a mapped endpoint");
    }

    private static void AssertAllReserved(IEnumerable<string> segments, string origin)
    {
        var missing = segments
            .Select(NormalizeAssetName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(segment => !ReservedSlugs.Contains(segment))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        missing.Should().BeEmpty(
            $"every {origin}'s first literal segment must be in Croft.Shared.ReservedSlugs, "
            + "so a page slug can never shadow it");
    }

    /// <summary>
    /// MapStaticAssets serves each asset under its plain name and under fingerprinted
    /// (app.xxxxxxxxxx.css) and precompressed (app.css.gz) variants — all three are the same
    /// file, so the variant suffixes are stripped before the name is checked.
    /// </summary>
    private static string NormalizeAssetName(string segment)
    {
        var name = segment.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? segment[..^3] : segment;
        return FingerprintToken().Replace(name, string.Empty);
    }

    [GeneratedRegex(@"\.[0-9a-z]{8,}(?=\.)")]
    private static partial Regex FingerprintToken();

    private static string? FirstLiteralSegment(string? pattern)
    {
        var first = pattern?.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is null || first.Contains('{') ? null : first;
    }
}
