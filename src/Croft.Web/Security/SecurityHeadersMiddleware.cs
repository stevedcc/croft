using Croft.Web.Security.Csp;

namespace Croft.Web.Security;

/// <summary>
/// The plain security headers every Web response carries. HSTS goes out outside Development on
/// every response whatever the scheme: TLS terminates at the ingress, so the app must not judge
/// by the request it sees, and the value pins HTTPS for a year including subdomains. Header
/// values are assigned, never appended, so each goes out exactly once.
/// Cross-Origin-Embedder-Policy goes on documents only, decided by the same rule the CSP uses
/// (<see cref="CspDocument.KindOf"/>): it governs what a document may embed, and the documents
/// load nothing cross-origin. Cross-Origin-Resource-Policy goes on every response: cross-origin
/// where the endpoint carries <see cref="CrossOriginResource"/> (what other sites and readers
/// fetch), same-origin everywhere else.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IHostEnvironment environment)
{
    public Task InvokeAsync(HttpContext context)
    {
        // Applied when the response starts, not before next(): UseExceptionHandler clears the response
        // (headers included) before it writes its 400/500/503, so headers assigned up front would be
        // missing from exactly those responses.
        context.Response.OnStarting(static state =>
        {
            var (httpContext, isDevelopment) = ((HttpContext, bool))state;
            Apply(httpContext, isDevelopment);
            return Task.CompletedTask;
        }, (context, environment.IsDevelopment()));

        return next(context);
    }

    private static void Apply(HttpContext context, bool isDevelopment)
    {
        var headers = context.Response.Headers;
        if (!isDevelopment)
        {
            headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        }

        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";

        headers["Cross-Origin-Resource-Policy"] =
            context.GetEndpoint()?.Metadata.GetMetadata<CrossOriginResource>() is null ? "same-origin" : "cross-origin";

        if (CspDocument.KindOf(context) is not CspDocumentKind.NotADocument)
        {
            headers["Cross-Origin-Embedder-Policy"] = "require-corp";
        }
    }
}
