// WORKAROUND-FILE(W-090, ASP.NET Core): neither the resource collection (@Assets) nor the asset descriptors carry an integrity digest, so it is read from the identity endpoints' ETag (the base64 SHA-256 MapStaticAssets computed)
using Microsoft.AspNetCore.StaticAssets;

namespace Croft.Web.Services;

/// <summary>
/// The Subresource Integrity digest of a static asset, for the <c>integrity</c> attribute of a
/// <c>&lt;script src&gt;</c>. <c>MapStaticAssets</c> hashes every file at build time and records
/// the digest on the asset's endpoint, so nothing is hashed at runtime. The component-side
/// resource collection (<c>@Assets</c>) maps a name to its fingerprinted URL but carries only
/// the label, and the endpoint descriptors drop the digest too; what they keep is the ETag of the
/// uncompressed variant, which MapStaticAssets sets to that same SHA-256 (quoted, base64). The
/// digest is read from there, keyed by the fingerprinted URL <c>@Assets</c> returns; the
/// ScriptIntegrityTests compare it with the bytes actually served, so a change of the ETag's
/// meaning cannot go unnoticed. The browser then refuses
/// a script whose bytes are not the build's, even from a directory the policy allows.
/// </summary>
public sealed class StaticAssetIntegrity(EndpointDataSource endpoints)
{
    private readonly Lazy<Dictionary<string, string>> _digests = new(() => Collect(endpoints));

    /// <summary>The digest ("sha256-…") of the asset served at <paramref name="url"/> (the
    /// fingerprinted URL <c>@Assets</c> returns). A script the build knows no digest for must not
    /// be rendered without one, so a missing digest throws.</summary>
    public string Of(string url) =>
        _digests.Value.TryGetValue(url.TrimStart('/'), out var digest)
            ? digest
            : throw new InvalidOperationException($"MapStaticAssets computed no integrity digest for '{url}'.");

    private static Dictionary<string, string> Collect(EndpointDataSource endpoints)
    {
        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var endpoint in endpoints.Endpoints)
        {
            // The identity (uncompressed) variant of the file: its ETag is the quoted base64
            // SHA-256 of the bytes, which is what an integrity attribute names.
            var asset = endpoint.Metadata.GetMetadata<StaticAssetDescriptor>();
            if (asset is null || asset.Selectors.Count > 0)
            {
                continue;
            }

            var etag = asset.ResponseHeaders.FirstOrDefault(header => header.Name == "ETag")?.Value;
            if (etag is { Length: > 2 } && etag[0] == '"' && etag[^1] == '"')
            {
                digests.TryAdd(asset.Route, $"sha256-{etag[1..^1]}");
            }
        }

        return digests;
    }
}
