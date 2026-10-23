namespace Croft.Web.Services;

/// <summary>What one "keys import" run did: the key names it imported and the ones it skipped.</summary>
public sealed record DataProtectionKeyImportResult(IReadOnlyList<string> Imported, IReadOnlyList<string> Skipped);
