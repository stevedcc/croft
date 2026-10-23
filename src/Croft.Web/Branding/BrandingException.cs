namespace Croft.Web;

/// <summary>
/// A branding folder the app cannot use: malformed <c>branding.json</c>, an invalid colour or an
/// unknown token. Web fails at startup rather than serving a half-styled site — a deployment
/// mistake, like a missing <c>Blog:BaseUrl</c>. The message names the offending field.
/// </summary>
public sealed class BrandingException(string message) : Exception(message);
