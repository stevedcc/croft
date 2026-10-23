namespace Croft.Api;

public enum StartupCommandKind
{
    Serve,
    Migrate,
    Export,
    Import,
    Invalid
}

/// <summary>
/// The API's command line: no arguments (or only host options such as <c>--urls</c>, which the
/// dev scripts pass) serves; <c>migrate</c>, <c>export --out &lt;dir&gt;</c> and
/// <c>import --from &lt;dir&gt;</c> are the console commands; anything else is invalid and must exit 1
/// instead of starting the web server inside a Job that would then never finish.
/// </summary>
public sealed record StartupCommand(StartupCommandKind Kind, string? Directory)
{
    public static StartupCommand Parse(string[] args) => args switch
    {
        [] => new(StartupCommandKind.Serve, null),
        [var first, ..] when first.StartsWith('-') => new(StartupCommandKind.Serve, null),
        ["migrate"] => new(StartupCommandKind.Migrate, null),
        ["export", "--out", { Length: > 0 } directory] => new(StartupCommandKind.Export, directory),
        ["import", "--from", { Length: > 0 } directory] => new(StartupCommandKind.Import, directory),
        _ => new(StartupCommandKind.Invalid, null)
    };
}
