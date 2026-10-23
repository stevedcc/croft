using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Croft.Web.Caching;

/// <summary>
/// Compression of the documents the output cache may store (brief 14c item 5): an anonymous
/// reader's reading pages, the feed and the sitemap, and nothing else. No admin or staff page,
/// nothing a signed-in reader gets, no error or not-found page, no <c>/api/media</c> image (already
/// compressed) and no static asset (<c>MapStaticAssets</c> serves its own precompressed files).
/// <para>
/// Why only those: they carry no secret (no antiforgery token, no user data, W-092), so compressing
/// them opens no BREACH-style leak, where a secret and attacker-chosen text share one compressed
/// response. A page that gets a token or a user's data must not be added here.
/// </para>
/// <para>
/// Compressed once, served many times: the framework's <c>ResponseCompressionMiddleware</c> runs
/// after the output cache (<see cref="ReadingPageCache"/>), so the cache stores the compressed bytes
/// and a hit compresses nothing. The encoding is negotiated once, before the cache
/// (<see cref="Negotiate"/>), and both the cache key (<see cref="VaryByEncoding"/>) and the
/// compressor (<see cref="ReadingPageCompressionProvider"/>) read that one answer, so an entry is
/// always the encoding its key says and at most three entries per page exist, never one per raw
/// <c>Accept-Encoding</c> value. This is the framework's middleware with its own
/// <see cref="IResponseCompressionProvider"/> because the stock provider picks the encoding from the
/// raw header (a key built from the same header could disagree with it) and decides by MIME type
/// alone, not by what the request is.
/// </para>
/// </summary>
public static class ReadingPageCompression
{
    public const string Brotli = "br";
    public const string Gzip = "gzip";
    public const string Identity = "identity";

    /// <summary>The key of the cache's vary rule for the encoding.</summary>
    private const string EncodingKey = "encoding";

    private static readonly object EncodingItem = new();

    /// <summary>Registers the Brotli and gzip providers (<see cref="CompressionLevel.Optimal"/>: the compressor runs once
    /// per entry per cache duration, so the best ratio is worth its time) and the provider that limits them.</summary>
    public static IServiceCollection AddReadingPageCompression(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Optimal);
        services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Optimal);
        services.AddSingleton<ICompressionProvider, BrotliCompressionProvider>();
        services.AddSingleton<ICompressionProvider, GzipCompressionProvider>();
        services.AddSingleton<IResponseCompressionProvider, ReadingPageCompressionProvider>();
        return services;
    }

    /// <summary>
    /// Runs before the output cache: for a document the cache may store, settles the encoding once
    /// (kept on the request for the cache key and the compressor) and says the response varies on it.
    /// </summary>
    public static IApplicationBuilder UseReadingPageCompressionNegotiation(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(static async (context, next) =>
        {
            if (IsCompressible(context))
            {
                context.Items[EncodingItem] = Negotiate(context.Request.Headers.AcceptEncoding);
                context.Response.OnStarting(static state =>
                {
                    var headers = ((HttpContext)state).Response.Headers;
                    if (!headers.Vary.Any(value => value is not null && value.Contains(HeaderNames.AcceptEncoding, StringComparison.OrdinalIgnoreCase)))
                    {
                        headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);
                    }

                    return Task.CompletedTask;
                }, context);
            }

            await next(context);
        });
    }

    /// <summary>Whether the request is for a document the output cache may store (the cache's own
    /// request tests, without the settings: a cache turned off still serves the same documents).</summary>
    internal static bool IsCompressible(HttpContext context) =>
        ReadingPageCachePolicy.IsPublicDocument(context, out _) || FeedCachePolicy.IsFeedDocument(context);

    /// <summary>The encoding settled for this request: <see cref="Brotli"/>, <see cref="Gzip"/> or
    /// <see cref="Identity"/> (also for any request that is not compressible).</summary>
    internal static string EncodingOf(HttpContext context) =>
        context.Items.TryGetValue(EncodingItem, out var encoding) ? (string)encoding! : Identity;

    /// <summary>Makes the cache key depend on the normalised encoding.</summary>
    internal static void VaryByEncoding(Microsoft.AspNetCore.OutputCaching.OutputCacheContext context) =>
        context.CacheVaryByRules.VaryByValues[EncodingKey] = EncodingOf(context.HttpContext);

    /// <summary>
    /// The encoding this server uses for an <c>Accept-Encoding</c> header: Brotli when it is accepted
    /// at least as willingly as gzip, else gzip, else none. A quality of 0 refuses an encoding and
    /// <c>*</c> stands for every encoding the header does not name.
    /// </summary>
    internal static string Negotiate(StringValues acceptEncoding)
    {
        if (!StringWithQualityHeaderValue.TryParseList(acceptEncoding, out var accepted) || accepted.Count == 0)
        {
            return Identity;
        }

        double QualityOf(string encoding)
        {
            var named = accepted.FirstOrDefault(a => string.Equals(a.Value.Value, encoding, StringComparison.OrdinalIgnoreCase));
            var any = accepted.FirstOrDefault(a => a.Value == "*");
            var entry = named ?? any;
            return entry is null ? 0 : entry.Quality ?? 1;
        }

        var (brotli, gzip) = (QualityOf(Brotli), QualityOf(Gzip));
        if (brotli > 0 && brotli >= gzip)
        {
            return Brotli;
        }

        return gzip > 0 ? Gzip : Identity;
    }
}

/// <summary>
/// Tells the framework's compression middleware what to compress and with what: the encoding settled
/// before the cache (<see cref="ReadingPageCompression"/>), and a response only when it is the 200
/// HTML or XML document the cache would store (no cookie set, nothing already encoded).
/// </summary>
public sealed class ReadingPageCompressionProvider(IEnumerable<ICompressionProvider> providers) : IResponseCompressionProvider
{
    private static readonly HashSet<string> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/html", "application/xml", "application/atom+xml",
    };

    public bool CheckRequestAcceptsCompression(HttpContext context) =>
        ReadingPageCompression.EncodingOf(context) != ReadingPageCompression.Identity;

    public ICompressionProvider? GetCompressionProvider(HttpContext context)
    {
        var encoding = ReadingPageCompression.EncodingOf(context);
        return providers.FirstOrDefault(p => string.Equals(p.EncodingName, encoding, StringComparison.OrdinalIgnoreCase));
    }

    public bool ShouldCompressResponse(HttpContext context)
    {
        var response = context.Response;
        return response.StatusCode == StatusCodes.Status200OK
            && StringValues.IsNullOrEmpty(response.Headers.ContentEncoding)
            && StringValues.IsNullOrEmpty(response.Headers.SetCookie)
            && StringValues.IsNullOrEmpty(response.Headers.ContentRange)
            && MediaTypeHeaderValue.TryParse(response.ContentType, out var type)
            && type.MediaType.HasValue && MediaTypes.Contains(type.MediaType.Value);
    }
}
