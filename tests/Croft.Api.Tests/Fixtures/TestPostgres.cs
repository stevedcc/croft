namespace Croft.Api.Tests.Fixtures;

/// <summary>
/// The choice between the shared Postgres Testcontainer and a server the run was given:
/// CROFT_TEST_POSTGRES holds a connection string when the suites must use an existing
/// Postgres instead of starting a container (Chargehand's verify lane has no container
/// runtime and sets it).
/// </summary>
internal static class TestPostgres
{
    /// <summary>Returns the trimmed connection string, or null when there is none to use.</summary>
    internal static string? GivenServer(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
