namespace Croft.Api.Services;

/// <summary>Outcome of one post-summary backfill run.</summary>
/// <param name="Skipped">True when another instance held the job lock, so this run did nothing.</param>
/// <param name="Filled">How many posts this run filled a missing summary for.</param>
public sealed record PostSummaryBackfillResult(bool Skipped, int Filled);
