namespace Croft.Web.Security.Csp;

/// <summary>
/// The policy of the response being built, one per request. Components and endpoints add
/// their sources through it — <c>context.Features.Get&lt;ICspFeature&gt;()!.Policy.Add("frame-src", "...")</c>
/// — any time before the response starts; the header follows the policy until then.
/// </summary>
public interface ICspFeature
{
    CspPolicy Policy { get; }

    /// <summary>
    /// A second policy the response carries only as <c>Content-Security-Policy-Report-Only</c>:
    /// directives a page does not yet satisfy but whose violations the site wants reported (with
    /// <c>Csp:ReportOnly</c> on, the whole policy is report-only and these merge into it). Empty
    /// means no second header.
    /// </summary>
    CspPolicy ReportOnlyPolicy { get; }
}
