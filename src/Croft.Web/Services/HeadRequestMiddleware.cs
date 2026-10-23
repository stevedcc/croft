// WORKAROUND-FILE(W-101, ASP.NET Core): endpoints mapped with MapGet and the Razor components router match GET only, so a HEAD request is a 404
namespace Croft.Web.Services;

/// <summary>
/// Answers a HEAD request as the GET it stands for: the same status and headers, no body. Routing matches HEAD only against endpoints that
/// list it, and almost none of ours do (MapGet, the Razor components), so link checkers and uptime monitors that use HEAD were told the
/// site is broken. The request runs the pipeline as a GET (so the output cache, rate limiting, antiforgery and circuit admission treat it
/// exactly as they treat the GET: a HEAD takes no shortcut past any of them, and the cache stores the page the GET would have) and the
/// body goes nowhere.
/// </summary>
internal sealed class HeadRequestMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await next(context);
            return;
        }

        var body = context.Response.Body;
        context.Request.Method = HttpMethods.Get;
        context.Response.Body = Stream.Null;
        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = body;
            context.Request.Method = HttpMethods.Head;
        }
    }
}
