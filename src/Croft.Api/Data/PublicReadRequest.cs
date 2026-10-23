namespace Croft.Api.Data;

/// <summary>
/// The one definition of a public read: a GET (or HEAD) request with no signed-in user. The
/// read-replica routing (BlogDbConnectionString) and the public read cache both key on it, so
/// the two can never disagree about which requests are public. Everything else — admin,
/// editor, a signed-in reader, any write, and anything without an HttpContext such as a
/// background service — is not a public read and uses the primary, uncached.
/// </summary>
public static class PublicReadRequest
{
    public static bool IsAnonymousRead(HttpContext? context) =>
        context is not null
        && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        && context.User.Identity?.IsAuthenticated != true;
}
