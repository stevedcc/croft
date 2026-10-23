namespace Croft.Api.Security;

/// <summary>
/// The plain security headers every API response carries, plus the API's own CSP: the API
/// serves JSON (and media file downloads), never HTML, so nothing may load and nothing may
/// frame it. HSTS goes out outside Development on every response whatever the scheme: TLS
/// terminates at the ingress, so the app must not judge by the request it sees. Header values
/// are assigned, never appended, so each goes out exactly once.
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
            var (response, isDevelopment) = ((HttpResponse, bool))state;
            Apply(response.Headers, isDevelopment);
            return Task.CompletedTask;
        }, (context.Response, environment.IsDevelopment()));

        return next(context);
    }

    private static void Apply(IHeaderDictionary headers, bool isDevelopment)
    {
        if (!isDevelopment)
        {
            headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        }

        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
    }
}
