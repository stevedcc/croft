using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Croft.Web.Security.Csp;

/// <summary>
/// Writes the one Content-Security-Policy header (or Content-Security-Policy-Report-Only when
/// Csp:ReportOnly is on) on every Web response, from a base policy merged with the site's
/// configured additions, the registered contributors and whatever this request adds through
/// the ICspFeature hook. Header values are assigned, never appended, so the header goes out
/// exactly once; Blazor's own frame-ancestors header is disabled (see Program.cs) so this is
/// the only CSP writer.
///
/// The contributors stay the single source of the policy, but they run once per variant, not once
/// per response: a variant is the contributors' cache keys (ICspContributor.CacheKey at the start;
/// the document kind and IDocumentCspContributor.CacheKey when the response starts), and the
/// finished header strings of a variant are kept. A response then costs a few key lookups and
/// header assignments, a static asset included. The mutable policy exists only for a caller that
/// asks the ICspFeature for it (built on first access), and for a contributor that does not
/// declare a key, which keeps the earlier behaviour: build, contribute and write per response.
/// </summary>
public sealed class CspMiddleware(
    RequestDelegate next,
    IOptions<CspOptions> options,
    IEnumerable<ICspContributor> contributors,
    IEnumerable<IDocumentCspContributor> documentContributors)
{
    private const string ReportingEndpointsValue = "csp=\"/csp-report\"";
    private const string ReportOnlyHeaderName = "Content-Security-Policy-Report-Only";

    /// <summary>A variant is a handful (admin or not, the document kinds, the sign-out page); the
    /// bound only stops a contributor with an unbounded key (say, one per Host header) from
    /// growing the cache: past it, a variant is built each time.</summary>
    private const int MaxCachedVariants = 64;

    private readonly CspPolicy _template = BuildPolicy(options.Value);
    private readonly ICspContributor[] _contributors = [.. contributors];
    private readonly IDocumentCspContributor[] _documentContributors = [.. documentContributors];
    private readonly bool _reportOnlyMode = options.Value.ReportOnly;
    private readonly string _headerName = options.Value.ReportOnly
        ? ReportOnlyHeaderName
        : "Content-Security-Policy";

    private readonly ConcurrentDictionary<string, (CspPolicy Policy, CspPolicy ReportOnlyPolicy)> _startSnapshots = [with(StringComparer.Ordinal)];
    private readonly ConcurrentDictionary<string, HeaderValues> _atStart = [with(StringComparer.Ordinal)];
    private readonly ConcurrentDictionary<(string StartKey, CspDocumentKind Kind, string DocumentKey), FinalHeaders> _atResponseStart = new();

    /// <summary>The finished value of the enforced header, and of the second (report-only) one when there is one.</summary>
    private sealed record HeaderValues(string Main, string? Secondary);

    /// <summary>The header values of a response once its kind is known, and whether they differ from the ones written at the start.</summary>
    private sealed record FinalHeaders(HeaderValues Values, bool ChangesTheStartHeader);

    public async Task InvokeAsync(HttpContext context)
    {
        var response = new CspResponse(this, context);
        context.Features.Set<ICspFeature>(response.Feature);
        response.WriteStartHeaders();
        context.Response.OnStarting(static state => ((CspResponse)state).OnStarting(), response);
        await next(context);
    }

    /// <summary>The CSP state of one response: its variant keys and, when something asked for it, its mutable policies.</summary>
    private sealed class CspResponse
    {
        private readonly CspMiddleware _owner;
        private readonly HttpContext _context;
        private string? _startKey;
        private CspDocumentKind? _kind;
        private (CspPolicy Policy, CspPolicy ReportOnlyPolicy)? _policies;

        public CspResponse(CspMiddleware owner, HttpContext context)
        {
            _owner = owner;
            _context = context;
            Feature = new CspFeature(Materialize);
        }

        public CspFeature Feature { get; }

        /// <summary>The header before routing has chosen the endpoint: the base policy and the request contributors.</summary>
        public void WriteStartHeaders()
        {
            _startKey = _owner.StartKeyOf(_context);
            if (_startKey is not null && _owner._atStart.TryGetValue(_startKey, out var cached))
            {
                _owner.Write(_context.Response.Headers, cached);
                return;
            }

            if (_startKey is null)
            {
                // A contributor without a key: build, contribute and write for this response, and
                // keep the policies, which the header follows from here on.
                var policies = Materialize();
                _owner.Write(_context.Response.Headers, _owner.ValuesOf(policies.Policy, policies.ReportOnlyPolicy));
                return;
            }

            var policy = _owner.BuildAtStart(_context, _startKey, out var reportOnlyPolicy);
            var values = _owner.ValuesOf(policy, reportOnlyPolicy);
            if (_owner._atStart.Count < MaxCachedVariants)
            {
                _owner._atStart.TryAdd(_startKey, values);
            }

            _owner.Write(_context.Response.Headers, values);
        }

        /// <summary>The header once the endpoint is known: the document contributors' sources.</summary>
        public Task OnStarting()
        {
            var kind = CspDocument.KindOf(_context);
            _kind = kind;
            if (_policies is { } existing)
            {
                // Something holds the mutable policies (a caller of the feature, or a contributor
                // without a key): the document contributors add to them and the header follows.
                _owner.ContributeDocument(_context, existing.Policy, existing.ReportOnlyPolicy, kind);
                return Task.CompletedTask;
            }

            var documentKey = _owner.DocumentKeyOf(_context, kind);
            if (_startKey is null || documentKey is null)
            {
                var policies = Materialize();
                _owner.ContributeDocument(_context, policies.Policy, policies.ReportOnlyPolicy, kind);
                return Task.CompletedTask;
            }

            var key = (_startKey, kind, documentKey);
            if (!_owner._atResponseStart.TryGetValue(key, out var final))
            {
                var policy = _owner.BuildAtStart(_context, _startKey, out var reportOnlyPolicy);
                var atStart = _owner.ValuesOf(policy, reportOnlyPolicy);
                _owner.ContributeDocument(_context, policy, reportOnlyPolicy, kind);
                var values = _owner.ValuesOf(policy, reportOnlyPolicy);
                final = new FinalHeaders(values, values != atStart);
                if (_owner._atResponseStart.Count < MaxCachedVariants)
                {
                    _owner._atResponseStart.TryAdd(key, final);
                }
            }

            // The header is rewritten only where the document stage added something, as it was when
            // the header followed the policy's changes: an endpoint that removed the header
            // itself (the site files) keeps it removed.
            if (final.ChangesTheStartHeader)
            {
                _owner.Write(_context.Response.Headers, final.Values);
            }

            return Task.CompletedTask;
        }

        private (CspPolicy Policy, CspPolicy ReportOnlyPolicy) Materialize()
        {
            if (_policies is { } existing)
            {
                return existing;
            }

            var policy = _owner.BuildAtStart(_context, _startKey, out var reportOnlyPolicy);
            _policies = (policy, reportOnlyPolicy);
            if (_kind is { } kind)
            {
                // The response's kind is known already (the feature was asked for late, from another
                // OnStarting callback): catch the policies up before they are handed out.
                _owner.ContributeDocument(_context, policy, reportOnlyPolicy, kind);
            }

            // A component or endpoint may add sources any time before the response starts — even
            // from another OnStarting callback — so the header follows the policies until then.
            void WritePolicy()
            {
                if (!_context.Response.HasStarted)
                {
                    _owner.Write(_context.Response.Headers, _owner.ValuesOf(policy, reportOnlyPolicy));
                }
            }

            policy.Changed += WritePolicy;
            reportOnlyPolicy.Changed += WritePolicy;
            return (policy, reportOnlyPolicy);
        }
    }

    /// <summary>The policies as the base policy and the request contributors leave them. The
    /// contributors run for a start key once; later builds (the document stage of each variant, a
    /// caller of the feature) copy the kept result, which is never handed out.</summary>
    private CspPolicy BuildAtStart(HttpContext context, string? startKey, out CspPolicy reportOnlyPolicy)
    {
        if (startKey is not null && _startSnapshots.TryGetValue(startKey, out var snapshot))
        {
            reportOnlyPolicy = new CspPolicy().Merge(snapshot.ReportOnlyPolicy);
            return new CspPolicy().Merge(snapshot.Policy);
        }

        var policy = new CspPolicy().Merge(_template);
        reportOnlyPolicy = new CspPolicy();
        foreach (var contributor in _contributors)
        {
            contributor.Contribute(context, policy);
        }

        if (startKey is not null && _startSnapshots.Count < MaxCachedVariants)
        {
            _startSnapshots.TryAdd(startKey, (new CspPolicy().Merge(policy), new CspPolicy().Merge(reportOnlyPolicy)));
        }

        return policy;
    }

    private void ContributeDocument(HttpContext context, CspPolicy policy, CspPolicy reportOnlyPolicy, CspDocumentKind kind)
    {
        foreach (var contributor in _documentContributors)
        {
            contributor.Contribute(context, policy, reportOnlyPolicy, kind);
        }
    }

    /// <summary>The request contributors' keys joined, or null when one of them declares none.</summary>
    private string? StartKeyOf(HttpContext context)
    {
        switch (_contributors.Length)
        {
            case 0:
                return "";
            case 1:
                return _contributors[0].CacheKey(context);
        }

        var keys = new string[_contributors.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            if (_contributors[i].CacheKey(context) is not { } key)
            {
                return null;
            }

            keys[i] = key;
        }

        return string.Join('|', keys);
    }

    private string? DocumentKeyOf(HttpContext context, CspDocumentKind kind)
    {
        switch (_documentContributors.Length)
        {
            case 0:
                return "";
            case 1:
                return _documentContributors[0].CacheKey(context, kind);
        }

        var keys = new string[_documentContributors.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            if (_documentContributors[i].CacheKey(context, kind) is not { } key)
            {
                return null;
            }

            keys[i] = key;
        }

        return string.Join('|', keys);
    }

    private HeaderValues ValuesOf(CspPolicy policy, CspPolicy reportOnlyPolicy)
    {
        if (_reportOnlyMode)
        {
            // Everything is report-only already: the second policy joins the one header.
            return new HeaderValues(new CspPolicy().Merge(policy).Merge(reportOnlyPolicy).ToString(), null);
        }

        var secondary = reportOnlyPolicy.ToString();
        return new HeaderValues(policy.ToString(), secondary.Length > 0 ? secondary : null);
    }

    private void Write(IHeaderDictionary headers, HeaderValues values)
    {
        headers[_headerName] = values.Main;
        if (!_reportOnlyMode)
        {
            if (values.Secondary is not null)
            {
                headers[ReportOnlyHeaderName] = values.Secondary;
            }
            else
            {
                headers.Remove(ReportOnlyHeaderName);
            }
        }

        headers["Reporting-Endpoints"] = ReportingEndpointsValue;
    }

    private static CspPolicy BuildPolicy(CspOptions options)
    {
        // Deny by default: default-src is 'none' and every directive a page needs is written out,
        // so a kind of resource nobody listed (media, prefetch, a worker, a frame) is refused.
        // img-src has no data: source: nothing this app (or MudBlazor, whose only data: images
        // are CSS backgrounds of an unused colour picker) renders loads one.
        // base-uri stays 'self' and not 'none': Blazor reads <base href="/"> in App.razor to
        // resolve its URLs, and base-uri 'none' makes the browser refuse that element.
        var policy = new CspPolicy()
            .Add("default-src", "'none'")
            .Add("script-src", "'none'")
            .Add("style-src", "'self'")
            .Add("style-src-elem", "'self'")
            .Add("style-src-attr", "'none'")
            .Add("img-src", "'self'")
            .Add("font-src", "'self'")
            .Add("connect-src", "'self'")
            .Add("manifest-src", "'self'")
            .Add("object-src", "'none'")
            .Add("base-uri", "'self'")
            .Add("form-action", "'self'")
            .Add("frame-ancestors", "'none'")
            .Add("frame-src", "'none'")
            .Add("worker-src", "'none'")
            .Add("report-uri", "/csp-report")
            .Add("report-to", "csp");

        foreach (var (directive, sources) in options.Add)
        {
            policy.Add(directive, [.. sources]);
        }

        return policy;
    }
}
