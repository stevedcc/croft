namespace Croft.Web.Security.Csp;

/// <summary>
/// The per-request hook (<see cref="ICspFeature"/>). The two mutable policies are built on first
/// access: a response whose header comes from the middleware's cache never needs them, so the
/// per-request copy of the policy is only made for the rare caller that adds a source.
/// </summary>
public sealed class CspFeature(Func<(CspPolicy Policy, CspPolicy ReportOnlyPolicy)> create) : ICspFeature
{
    private (CspPolicy Policy, CspPolicy ReportOnlyPolicy)? _policies;

    /// <summary>Whether a caller (or the middleware) has asked for the mutable policies.</summary>
    public bool IsMaterialized => _policies is not null;

    public CspPolicy Policy => Policies.Policy;

    public CspPolicy ReportOnlyPolicy => Policies.ReportOnlyPolicy;

    private (CspPolicy Policy, CspPolicy ReportOnlyPolicy) Policies => _policies ??= create();
}
