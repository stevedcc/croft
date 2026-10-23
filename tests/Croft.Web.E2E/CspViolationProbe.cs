using System.Text.Json;
using System.Text.Json.Serialization;

namespace Croft.Web.E2E;

/// <summary>One <c>securitypolicyviolation</c> event as the browser reports it. <c>Sample</c> is
/// the event's own sample (empty without 'report-sample' in the policy); <c>BlockedContent</c> is
/// the probe's read of the offending element's text or style attribute.</summary>
public sealed record CspViolation(
    string Step,
    string Directive,
    string BlockedUri,
    string Sample,
    string BlockedContent,
    string SourceFile,
    int LineNumber,
    string Disposition)
{
    public override string ToString() =>
        $"[{Step}] {Directive} | blocked-uri: {BlockedUri} | sample: '{Sample}'"
        + (BlockedContent.Length > 0 ? $" | blocked content: '{BlockedContent}'" : string.Empty)
        + (SourceFile.Length > 0 ? $" | at {SourceFile}:{LineNumber}" : string.Empty)
        + $" | {Disposition}";
}

/// <summary>One CSP error as Playwright's console listener sees it (Chrome's "Refused to …" line).</summary>
public sealed record CspConsoleError(string Step, string Text)
{
    public override string ToString() => $"[{Step}] {Text}";
}

/// <summary>
/// The measurement probe for the CSP E2E (tests/Croft.Web.E2E/CspTests.cs). An init script
/// installs a capturing <c>securitypolicyviolation</c> listener in every new document before any
/// page script runs, so even the head's inline scripts are caught, and buffers each event
/// (directive, blocked-uri, sample, source, disposition) on <c>window.__cspProbeEvents</c>. A
/// Playwright console listener records Chrome's own CSP errors in parallel (the brief's second
/// channel). Steps label the recording: <see cref="StepAsync"/> drains what the previous step
/// produced and names the next one, so every violation carries the page it happened on.
/// </summary>
public sealed class CspViolationProbe
{
    // WORKAROUND(W-070, Chromium): event.sample is empty without report-sample, so the probe reads the offending element instead
    private const string InitScript = """
        (() => {
            if (window.__cspProbe) { return; }
            window.__cspProbe = true;
            window.__cspProbeEvents = [];
            document.addEventListener('securitypolicyviolation', (event) => {
                // Chrome fills event.sample only when the policy carries 'report-sample' (which
                // this policy must not grow just for the measurement), so the blocked content is
                // read off the offending element: the <style>/<script> text or the style attribute
                // — and when there is nothing to read (e.g. an empty style=""), the element's own
                // identity (tag + classes), so the fixing work knows which component to chase.
                const target = event.target;
                let blockedContent = event.sample || '';
                if (!blockedContent && target) {
                    if (target.tagName === 'STYLE' || target.tagName === 'SCRIPT') {
                        blockedContent = (target.textContent || '').trim();
                    } else if (typeof target.getAttribute === 'function') {
                        blockedContent = target.getAttribute('style') || '';
                        if (!blockedContent && typeof target.className === 'string') {
                            blockedContent = '<' + target.tagName.toLowerCase()
                                + (target.className.trim() ? '.' + target.className.trim().replace(/\s+/g, '.') : '')
                                + '>';
                        }
                    }
                }
                window.__cspProbeEvents.push({
                    directive: event.effectiveDirective || event.violatedDirective,
                    blockedUri: event.blockedURI,
                    sample: event.sample || '',
                    blockedContent: blockedContent.slice(0, 60),
                    sourceFile: event.sourceFile || '',
                    lineNumber: event.lineNumber || 0,
                    disposition: event.disposition
                });
            }, true);
        })();
        """;
    // END WORKAROUND(W-070)

    private const string DrainScript = """
        () => {
            const events = window.__cspProbeEvents || [];
            window.__cspProbeEvents = [];
            return JSON.stringify(events);
        }
        """;

    private readonly IPage _page;
    private readonly List<CspViolation> _violations = [];
    private readonly List<CspViolation> _trustedTypesReports = [];
    private readonly List<CspConsoleError> _consoleErrors = [];
    private string _step = "before the first step";

    private CspViolationProbe(IPage page) => _page = page;

    /// <summary>Every violation of the ENFORCED policy recorded so far (across all steps and
    /// documents). The reading pages' report-only Trusted Types reports are not among them: they
    /// are expected from blazor.web.js (W-091) and kept in <see cref="TrustedTypesReports"/>, where
    /// the tests check that no script of the site's own appears.</summary>
    public IReadOnlyList<CspViolation> Violations => _violations;

    /// <summary>The report-only Trusted Types violations recorded so far.</summary>
    public IReadOnlyList<CspViolation> TrustedTypesReports => _trustedTypesReports;

    /// <summary>Every CSP console error recorded so far.</summary>
    public IReadOnlyList<CspConsoleError> ConsoleErrors => _consoleErrors;

    /// <summary>Installs the init script and the console listener on <paramref name="page"/>.</summary>
    public static async Task<CspViolationProbe> AttachAsync(IPage page)
    {
        var probe = new CspViolationProbe(page);
        await page.AddInitScriptAsync(InitScript);
        page.Console += probe.OnConsole;
        return probe;
    }

    /// <summary>Ends the current step (draining its events) and labels everything after this call
    /// with <paramref name="label"/>.</summary>
    public async Task StepAsync(string label)
    {
        await DrainAsync();
        _step = label;
    }

    /// <summary>Pulls the buffered events out of the current document and files them under the
    /// current step. Events kept only in the page would be lost on the next full navigation.</summary>
    public async Task<IReadOnlyList<CspViolation>> DrainAsync()
    {
        var json = await _page.EvaluateAsync<string>(DrainScript);
        var raw = JsonSerializer.Deserialize<List<RawViolation>>(json) ?? [];
        var drained = raw.Select(violation => new CspViolation(
            _step, violation.Directive, violation.BlockedUri, violation.Sample, violation.BlockedContent,
            violation.SourceFile, violation.LineNumber, violation.Disposition)).ToList();
        _violations.AddRange(drained.Where(violation => !IsTrustedTypesReport(violation)));
        _trustedTypesReports.AddRange(drained.Where(IsTrustedTypesReport));
        return drained;
    }

    /// <summary>The full finding list for a failed run: every violation (directive, blocked URI,
    /// sample) and every console error, then the same violations grouped by what they are so the
    /// fixing work can be ordered.</summary>
    public static string FormatFindings(
        IReadOnlyList<CspViolation> violations,
        IReadOnlyList<CspConsoleError> consoleErrors)
    {
        var lines = new List<string>
        {
            $"CSP violations: {violations.Count} (asserted: zero)",
            "--- full list (every violation: directive, blocked URI, sample) ---",
        };
        lines.AddRange(violations.Count == 0
            ? ["(none)"]
            : violations.Select(violation => violation.ToString()));

        lines.Add($"--- console CSP errors: {consoleErrors.Count} ---");
        lines.AddRange(consoleErrors.Count == 0
            ? ["(none)"]
            : consoleErrors.Select(error => error.ToString()));

        lines.Add("--- violations grouped by (directive, blocked-uri, sample/blocked content) ---");
        var groups = violations
            .GroupBy(violation => (violation.Directive, violation.BlockedUri, violation.Sample, violation.BlockedContent))
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.Directive, StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var steps = group.Select(violation => violation.Step).Distinct().ToList();
            lines.Add($"{group.Count()}x {group.Key.Directive} | blocked-uri: {group.Key.BlockedUri}"
                + $" | sample: '{group.Key.Sample}' | blocked content: '{group.Key.BlockedContent}'");
            lines.Add($"    on: {string.Join(", ", steps)}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static bool IsTrustedTypesReport(CspViolation violation) =>
        violation.Directive == "require-trusted-types-for" && violation.Disposition == "report";

    private void OnConsole(object? sender, IConsoleMessage message)
    {
        var text = message.Text;
        if (text.Contains("The policy is report-only", StringComparison.Ordinal)
            || (text.Contains("[Report Only]", StringComparison.Ordinal)
                && text.Contains("require-trusted-types-for", StringComparison.Ordinal)))
        {
            // The reading pages' report-only Trusted Types policy (W-091) logs one such line per
            // report (Chromium: "The policy is report-only"; WebKit: "[Report Only] This requires a
            // TrustedHTML value…"); the reports themselves are kept in TrustedTypesReports.
            return;
        }

        if (text.Contains("Content Security Policy", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Refused to", StringComparison.Ordinal))
        {
            _consoleErrors.Add(new CspConsoleError(_step, text));
        }
    }

    private sealed record RawViolation(
        [property: JsonPropertyName("directive")] string Directive,
        [property: JsonPropertyName("blockedUri")] string BlockedUri,
        [property: JsonPropertyName("sample")] string Sample,
        [property: JsonPropertyName("blockedContent")] string BlockedContent,
        [property: JsonPropertyName("sourceFile")] string SourceFile,
        [property: JsonPropertyName("lineNumber")] int LineNumber,
        [property: JsonPropertyName("disposition")] string Disposition);
}
