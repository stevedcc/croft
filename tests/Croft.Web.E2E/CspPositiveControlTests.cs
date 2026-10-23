namespace Croft.Web.E2E;

/// <summary>
/// The two-sided half of the CSP measurements (test-suite review I-7). The measurement tests
/// assert that a page raises NO violation; an empty list is exactly what a looser policy also
/// produces, so on their own they cannot tell a policy that holds from one that was widened. Each
/// case here does the forbidden thing on a real reading page and requires the browser to refuse it:
/// a <c>securitypolicyviolation</c> of the matching directive with disposition <c>enforce</c>, and
/// the refused thing having no effect. Widen the policy for any of these directives and the matching
/// case fails, where every "zero violations" measurement stays green.
/// Category=Csp, any engine (<c>BROWSER=…</c>); API fixtures only.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
[Category("Csp")]
public class CspPositiveControlTests : StaticReadingTest
{
    private const string Foreign = "https://csp-control.invalid";

    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    private static string _postSlug = string.Empty;

    /// <summary>The forbidden act, the directive that must refuse it, and a script returning true when the act had no effect.</summary>
    private static readonly (string Name, string Act, string Directive, string Blocked, string NoEffect)[] Controls =
    [
        ("an injected <style> element", "() => { const s = document.createElement('style'); s.textContent = 'body { outline: 7px solid rgb(1, 2, 3) }'; document.head.appendChild(s); }",
            "style-src-elem", "inline", "() => getComputedStyle(document.body).outlineWidth !== '7px'"),
        ("a style attribute set from script", "() => { document.body.setAttribute('style', 'outline: 7px solid rgb(1, 2, 3)'); }",
            "style-src-attr", "inline", "() => getComputedStyle(document.body).outlineWidth !== '7px'"),
        ("an image from another origin", $"() => {{ const i = new Image(); i.id = 'csp-control-img'; i.src = '{Foreign}/pixel.png'; document.body.appendChild(i); }}",
            "img-src", "csp-control.invalid", "() => document.getElementById('csp-control-img').naturalWidth === 0"),
        ("a fetch to another origin", $"() => {{ window.__fetchSettled = fetch('{Foreign}/data').then(() => 'resolved', () => 'rejected'); }}",
            "connect-src", "csp-control.invalid", "async () => (await window.__fetchSettled) === 'rejected'"),
        ("a font from another origin", $"() => {{ window.__fontSettled = new FontFace('csp-control', 'url({Foreign}/f.woff2)').load().then(() => 'loaded', () => 'failed'); }}",
            "font-src", "csp-control.invalid", "async () => (await window.__fontSettled) === 'failed'"),
        ("an inline script element", "() => { const s = document.createElement('script'); s.textContent = 'window.__inlineRan = true'; document.head.appendChild(s); }",
            "script-src-elem", "inline", "() => window.__inlineRan !== true"),
        ("an inline event handler", "() => { const b = document.createElement('button'); b.id = 'csp-control-handler'; b.setAttribute('onclick', 'window.__handlerRan = true'); document.body.appendChild(b); b.click(); }",
            "script-src-attr", "inline", "() => window.__handlerRan !== true"),
        ("an iframe", "() => { const f = document.createElement('iframe'); f.src = '/'; document.body.appendChild(f); }",
            "frame-src", "localhost", "() => { const f = document.querySelector('iframe'); return f !== null && !f.contentDocument?.querySelector('#sidebar-nav'); }"),
    ];

    public static IEnumerable<TestCaseData> ControlCases() =>
        Controls.Select(c => new TestCaseData(c.Name, c.Act, c.Directive, c.Blocked, c.NoEffect).SetName($"Policy_Refuses_{c.Directive}_{c.Name.Replace(' ', '_').Replace("<", string.Empty).Replace(">", string.Empty)}"));

    [TestCaseSource(nameof(ControlCases))]
    public async Task ThePolicy_RefusesWhatItForbids_AndSaysSo(string what, string act, string directive, string blocked, string noEffect)
    {
        await EnsureFixturesAsync();
        var probe = await CspViolationProbe.AttachAsync(Page);
        await Page.GotoAsync($"{BaseUrl}/post/{_postSlug}");
        await WaitForInteractiveAsync(Page);
        await probe.DrainAsync();
        Assert.That(probe.Violations, Is.Empty, "the page itself is clean before the forbidden act");

        await probe.StepAsync(what);
        await Page.EvaluateAsync(act);
        var violations = await WaitForViolationAsync(probe, directive, what);

        Assert.That(violations, Has.Some.Matches<CspViolation>(v =>
                v.Directive == directive && v.Disposition == "enforce" && v.BlockedUri.Contains(blocked, StringComparison.Ordinal)),
            $"{what} must be refused by {directive} with an enforced violation; got: {string.Join("; ", violations)}");
        Assert.That(await Page.EvaluateAsync<bool>(noEffect), Is.True, $"{what} must have no effect: the policy refused it");
    }

    /// <summary>
    /// Waits, bounded, for the browser to report a violation of <paramref name="directive"/>; when none
    /// comes, fails naming every violation that did (a control that fails to raise anything and one that
    /// raises the wrong directive need different fixes).
    /// </summary>
    private static async Task<IReadOnlyList<CspViolation>> WaitForViolationAsync(CspViolationProbe probe, string directive, string what)
    {
        var seen = new List<CspViolation>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            seen.AddRange(await probe.DrainAsync());
            if (seen.Any(v => v.Directive == directive))
            {
                return seen;
            }

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"{what} raised no {directive} violation within 15 s; violations seen: "
                    + (seen.Count == 0 ? "none" : string.Join("; ", seen)));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    private async Task EnsureFixturesAsync()
    {
        if (_postSlug.Length > 0)
        {
            return;
        }

        await DataGate.WaitAsync();
        try
        {
            if (_postSlug.Length == 0)
            {
                _postSlug = (await PublishAsync($"Csp Control Post {RunId}", BodyWithSections(RunId, 2))).Slug;
            }
        }
        finally
        {
            DataGate.Release();
        }
    }
}
