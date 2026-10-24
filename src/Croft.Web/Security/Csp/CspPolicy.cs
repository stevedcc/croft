namespace Croft.Web.Security.Csp;

/// <summary>
/// A Content Security Policy under construction: each directive maps to an ordered set of
/// sources. Sources union into a directive without duplicates and keep the order they were
/// added in, and a source added to a directive holding only 'none' drops the 'none', because
/// CSP requires 'none' to stand alone. ToString() is the header value: the directives come out
/// in one fixed order so the value is stable for tests and caches, and directives outside that
/// order are appended sorted. Changed fires whenever the policy actually mutates, so a writer
/// (the middleware) can keep a response header in sync until the response starts.
/// </summary>
public sealed class CspPolicy
{
    private static readonly string[] KnownDirectives =
    [
        "default-src", "script-src", "style-src", "style-src-elem", "style-src-attr",
        "img-src", "font-src", "connect-src", "manifest-src", "object-src", "base-uri", "form-action",
        "frame-ancestors", "frame-src", "worker-src", "require-trusted-types-for", "trusted-types",
        "report-uri", "report-to",
    ];

    /// <summary>Whether <paramref name="directive"/> is one of the directives this policy can
    /// actually carry — the fixed vocabulary reports must be reduced to before they become
    /// metric attributes.</summary>
    public static bool IsKnownDirective(string directive)
        => KnownDirectives.Contains(directive, StringComparer.Ordinal);

    private readonly Dictionary<string, List<string>> _sources = [with(StringComparer.Ordinal)];

    public event Action? Changed;

    public CspPolicy Add(string directive, params ReadOnlySpan<string> sources)
    {
        if (sources.IsEmpty)
        {
            return this;
        }

        if (!_sources.TryGetValue(directive, out var list))
        {
            _sources[directive] = list = [];
        }

        if (list is ["'none'"])
        {
            list.Clear();
        }

        var changed = false;
        foreach (var source in sources)
        {
            if (list.Contains(source))
            {
                continue;
            }

            list.Add(source);
            changed = true;
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return this;
    }

    /// <summary>
    /// Unions every directive of <paramref name="other"/> into this policy. The site's
    /// configured additions and the per-request copies of the base policy all go through this,
    /// so additions can never remove a source.
    /// </summary>
    public CspPolicy Merge(CspPolicy other)
    {
        foreach (var (directive, sources) in other._sources)
        {
            Add(directive, [.. sources]);
        }

        return this;
    }

    public override string ToString() =>
        string.Join("; ", OrderedDirectives().Select(directive => $"{directive} {string.Join(' ', _sources[directive])}"));

    private IEnumerable<string> OrderedDirectives() =>
        KnownDirectives.Where(_sources.ContainsKey)
            .Concat(_sources.Keys
                .Where(directive => !KnownDirectives.Contains(directive))
                .OrderBy(directive => directive, StringComparer.Ordinal));
}
