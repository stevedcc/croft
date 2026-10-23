namespace Croft.Web.Services;

/// <summary>The outcome of one expired-session purge tick: whether this instance ran it, and how many rows it removed.</summary>
public sealed record SessionPurgeResult(bool Skipped, int Purged);
