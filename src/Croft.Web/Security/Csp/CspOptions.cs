namespace Croft.Web.Security.Csp;

/// <summary>
/// The <c>Csp</c> configuration section. <see cref="ReportOnly"/> (default false: the policy
/// ships enforced) picks between the enforcing and the report-only header name; a site starts
/// its rollout with <c>Csp:ReportOnly=true</c>, watches the reports, then drops the setting.
/// <see cref="Add"/> holds the site's own additions, <c>Csp:Add:&lt;directive&gt;</c> = array
/// of sources; they merge into the base policy, never remove from it.
/// </summary>
public sealed class CspOptions
{
    public const string SectionName = "Csp";

    public bool ReportOnly { get; set; }

    /// <summary>How many reports one client IP may post per minute before it is throttled.</summary>
    public int ReportRateLimitPerMinute { get; set; } = 60;

    /// <summary>How many distinct clients (an IPv4 address or an IPv6 /64) the limiter tracks at once; when the table is full, new clients are refused until the sweep frees space.</summary>
    public int ReportMaxClients { get; set; } = 10_000;

    /// <summary>How many reports this pod accepts per minute from all clients together.</summary>
    public int ReportGlobalPerMinute { get; set; } = 600;

    public Dictionary<string, string[]> Add { get; set; } = [];
}
