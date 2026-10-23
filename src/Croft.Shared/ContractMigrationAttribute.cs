namespace Croft.Shared;

/// <summary>
/// Marks a migration that contracts the schema (drops, renames, or tightens a column the previous
/// release still reads). Such a migration is only safe once no deployed release reads the old
/// shape; the reason records why that holds now. Without this attribute the migration guard test
/// rejects contracting operations (expand/contract rule in AGENTS.md).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ContractMigrationAttribute(string reason) : Attribute
{
    /// <summary>Why the contraction is safe now (which release stopped reading the old shape).</summary>
    public string Reason { get; } = reason;
}
