using System.Reflection;
using System.Text.RegularExpressions;
using Croft.Shared;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Croft.Api.Tests.Fixtures;

/// <summary>
/// Reads the compiled migrations and reports contracting operations, enforcing the expand/contract
/// rule (AGENTS.md): a migration may add tables, nullable columns, columns with defaults and
/// indexes; it may not drop, rename, or make NOT NULL anything the previous release reads (raw SQL is
/// read as text for the same words; a NOT NULL column with no default is flagged as well). A
/// migration is exempt when it predates the rule (see <see cref="BaselineMigrationIds"/>) or its
/// class carries <see cref="ContractMigrationAttribute"/>. Each migration is read by instantiating
/// its class against a fresh MigrationBuilder("Npgsql") and inspecting the operations its Up
/// method builds: Up is protected, and the alternative (IMigrationsModelDiffer) diffs whole
/// models, which cannot attribute a change to the one migration that introduced it.
/// </summary>
internal static partial class MigrationContractGuard
{
    /// <summary>
    /// Migrations already present when the rule was adopted. They may contract: the rule protects
    /// the releases after it, and their shape was settled before it existed.
    /// </summary>
    public static readonly IReadOnlySet<string> BaselineMigrationIds =
        new HashSet<string>(StringComparer.Ordinal) { "20261006152151_InitialCreate" };

    /// <summary>
    /// Returns one description per contracting operation found in migrations that are neither in
    /// <paramref name="baselineMigrationIds"/> nor carry [ContractMigration("why it is safe now")].
    /// </summary>
    public static IReadOnlyList<string> FindViolations(
        IEnumerable<Type> migrationTypes, IEnumerable<string> baselineMigrationIds)
    {
        var violations = new List<string>();
        foreach (var migrationType in migrationTypes)
        {
            var id = migrationType.GetCustomAttribute<MigrationAttribute>()?.Id ?? migrationType.Name;
            if (baselineMigrationIds.Contains(id)
                || migrationType.GetCustomAttribute<ContractMigrationAttribute>() is not null)
            {
                continue;
            }

            foreach (var operation in ReadUpOperations(migrationType))
            {
                if (DescribeViolation(id, operation) is { } violation)
                {
                    violations.Add(violation);
                }
            }
        }

        return violations;
    }

    private static IReadOnlyList<MigrationOperation> ReadUpOperations(Type migrationType)
    {
        var migration = (Migration)(Activator.CreateInstance(migrationType)
            ?? throw new InvalidOperationException($"{migrationType.Name} could not be instantiated."));
        // WORKAROUND(W-059, EF Core): Migration.Up is protected, so the guard calls it by reflection
        var up = migrationType.GetMethod("Up", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{migrationType.Name} has no Up method to read.");
        var builder = new MigrationBuilder("Npgsql");
        up.Invoke(migration, [builder]);
        // END WORKAROUND(W-059)
        return [.. builder.Operations];
    }

    private static string? DescribeViolation(string migrationId, MigrationOperation operation) => operation switch
    {
        DropTableOperation drop => $"{migrationId}: DropTable({drop.Name})",
        DropColumnOperation drop => $"{migrationId}: DropColumn({drop.Table}.{drop.Name})",
        RenameColumnOperation rename => $"{migrationId}: RenameColumn({rename.Table}.{rename.Name})",
        RenameTableOperation rename => $"{migrationId}: RenameTable({rename.Name})",
        // Any AlterColumn(..., nullable: false) is a potential tightening of a column the previous
        // release reads, whatever else it also changes; flag it and require the attribute.
        AlterColumnOperation { IsNullable: false } alter
            => $"{migrationId}: AlterColumn({alter.Table}.{alter.Name}, nullable: false)",
        // A new or smaller max length rejects values the previous release may still write; so does a type change.
        AlterColumnOperation { MaxLength: { } newLength } alter
            when alter.OldColumn.MaxLength is not { } oldLength || oldLength > newLength
            => $"{migrationId}: AlterColumn({alter.Table}.{alter.Name}, max length {newLength})",
        // (a bounded column's type string changes with its length; the arm above judges that)
        AlterColumnOperation { ColumnType: { } newType, MaxLength: null } alter
            when alter.OldColumn.ColumnType is { } oldType && oldType != newType
            => $"{migrationId}: AlterColumn({alter.Table}.{alter.Name}, type {newType})",
        // Raw SQL is read as text: a statement that drops, renames or tightens is a contraction whatever
        // EF operation would have said it (the rule's escape hatch for "EF has no operation for it").
        SqlOperation sql when ContractingSql().IsMatch(sql.Sql) => $"{migrationId}: Sql({Excerpt(sql.Sql)})",
        // A NOT NULL column with no default breaks the previous release's INSERTs, which do not know it.
        AddColumnOperation { IsNullable: false, DefaultValue: null, DefaultValueSql: null, ComputedColumnSql: null } add
            => $"{migrationId}: AddColumn({add.Table}.{add.Name}, nullable: false, no default)",
        _ => null,
    };

    /// <summary>DROP, RENAME, SET NOT NULL and ALTER COLUMN ... TYPE, in any case, across line breaks.</summary>
    [GeneratedRegex(@"\b(DROP|RENAME)\b|SET\s+NOT\s+NULL|ALTER\s+COLUMN\b.*\bTYPE\b",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ContractingSql();

    private static string Excerpt(string sql)
    {
        var oneLine = Regex.Replace(sql, @"\s+", " ").Trim();
        return oneLine.Length <= 80 ? oneLine : oneLine[..80] + "...";
    }
}
