using System.Reflection;
using Croft.Api.Data;
using Croft.Api.Tests.Fixtures;
using Croft.Shared;
using Croft.Web.Data;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Croft.Api.Tests;

/// <summary>
/// Enforces the expand/contract rule (AGENTS.md) over the compiled migrations of BOTH apps: a
/// migration may add tables, nullable columns, columns with defaults and indexes; it may not
/// drop, rename, or make NOT NULL anything the previous release reads, unless its class carries
/// [ContractMigration("why it is safe now")] or the migration predates the rule. Web migrations
/// follow the same rule: add only.
/// </summary>
public class MigrationContractGuardTests
{
    [Fact]
    public void CompiledMigrations_ContainNoContractingOperations()
    {
        var violations = MigrationContractGuard.FindViolations(
            MigrationTypesIn(typeof(BlogDbContext).Assembly)
                .Concat(MigrationTypesIn(typeof(CroftWebDbContext).Assembly)),
            MigrationContractGuard.BaselineMigrationIds);

        violations.Should().BeEmpty("every migration must be expand-only or carry [ContractMigration]");
    }

    [Theory]
    [InlineData(typeof(DropTableMigration), "DropTable")]
    [InlineData(typeof(DropColumnMigration), "DropColumn")]
    [InlineData(typeof(RenameColumnMigration), "RenameColumn")]
    [InlineData(typeof(RenameTableMigration), "RenameTable")]
    [InlineData(typeof(TightenNullableMigration), "AlterColumn")]
    public void Guard_FlagsContractingOperations(Type migrationType, string expectedOperation)
    {
        var violations = MigrationContractGuard.FindViolations([migrationType], []);

        violations.Should().ContainSingle()
            .Which.Should().Contain(expectedOperation);
    }

    [Fact]
    public void Guard_AllowsAnAdditiveMigration()
    {
        var violations = MigrationContractGuard.FindViolations([typeof(AdditiveMigration)], []);

        violations.Should().BeEmpty("tables, nullable columns, defaulted columns and indexes are expand-only");
    }

    [Fact]
    public void Guard_AllowsAlterColumnThatRelaxesNullable()
    {
        var violations = MigrationContractGuard.FindViolations([typeof(RelaxNullableMigration)], []);

        violations.Should().BeEmpty("making a column nullable cannot break the previous release");
    }

    [Fact]
    public void Guard_ExemptsMigrationCarryingContractMigrationAttribute()
    {
        var violations = MigrationContractGuard.FindViolations([typeof(ContractedDropColumnMigration)], []);

        violations.Should().BeEmpty("the attribute records why the contraction is safe now");
    }

    [Fact]
    public void Guard_ExemptsMigrationsInTheBaseline()
    {
        var violations = MigrationContractGuard.FindViolations(
            [typeof(DropColumnMigration)], [nameof(DropColumnMigration)]);

        violations.Should().BeEmpty("migrations that predate the rule are exempt");
    }

    [Theory]
    [InlineData(typeof(RawDropColumnMigration))]
    [InlineData(typeof(RawLowerCaseRenameMigration))]
    [InlineData(typeof(RawSetNotNullMigration))]
    [InlineData(typeof(RawAlterColumnTypeMigration))]
    public void Guard_FlagsRawSqlThatContracts(Type migrationType)
    {
        var violations = MigrationContractGuard.FindViolations([migrationType], []);

        violations.Should().ContainSingle().Which.Should().Contain("Sql");
    }

    [Fact]
    public void Guard_AllowsRawSqlThatOnlyExpandsOrBackfills()
    {
        var violations = MigrationContractGuard.FindViolations([typeof(RawBackfillMigration)], []);

        violations.Should().BeEmpty("an UPDATE and an ADD COLUMN IF NOT EXISTS ... NULL contract nothing");
    }

    [Fact]
    public void Guard_ExemptsRawSqlInAMigrationCarryingTheAttribute()
    {
        var violations = MigrationContractGuard.FindViolations([typeof(ContractedRawDropColumnMigration)], []);

        violations.Should().BeEmpty("the attribute overrides the raw-SQL rule like every other");
    }

    [Fact]
    public void Guard_FlagsAddColumnThatIsNotNullableAndHasNoDefault()
    {
        var violations = MigrationContractGuard.FindViolations([typeof(NotNullWithoutDefaultMigration)], []);

        violations.Should().ContainSingle().Which.Should().Contain("AddColumn").And.Contain("Posts.Slug2");
    }

    [Fact]
    public void Guard_AllowsAddColumnThatIsNotNullableWhenItHasAValueOrSqlDefault()
    {
        var violations = MigrationContractGuard.FindViolations([typeof(NotNullWithDefaultsMigration)], []);

        violations.Should().BeEmpty("the previous release's INSERTs still succeed when the column has a default");
    }

    private static IEnumerable<Type> MigrationTypesIn(Assembly assembly) =>
        assembly.GetTypes().Where(type => typeof(Migration).IsAssignableFrom(type) && type is { IsClass: true, IsAbstract: false });

    private sealed class DropTableMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.DropTable(name: "LegacyNotes");
    }

    private sealed class DropColumnMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.DropColumn(name: "Summary", table: "Posts");
    }

    private sealed class RenameColumnMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.RenameColumn(name: "Summary", table: "Posts", newName: "Excerpt");
    }

    private sealed class RenameTableMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.RenameTable(name: "Posts", newName: "Articles");
    }

    private sealed class TightenNullableMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.AlterColumn<string>(
                name: "Summary", table: "Posts", type: "text", nullable: false,
                oldClrType: typeof(string), oldType: "text", oldNullable: true);
    }

    private sealed class RelaxNullableMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.AlterColumn<string>(
                name: "Summary", table: "Posts", type: "text", nullable: true,
                oldClrType: typeof(string), oldType: "text", oldNullable: false);
    }

    private sealed class AdditiveMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReadingLists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValue: new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)),
                },
                constraints: table => table.PrimaryKey("PK_ReadingLists", x => x.Id));
            migrationBuilder.AddColumn<string>(name: "Subtitle", table: "Posts", type: "text", nullable: true);
            migrationBuilder.AddColumn<int>(name: "ViewCount", table: "Posts", type: "integer", nullable: false, defaultValue: 0);
            migrationBuilder.CreateIndex(name: "IX_Posts_Subtitle", table: "Posts", column: "Subtitle");
        }
    }

    [ContractMigration("Summary moved to MediaAssets; no deployed release reads Posts.Summary after 0.2.x")]
    private sealed class ContractedDropColumnMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.DropColumn(name: "Summary", table: "Posts");
    }

    private sealed class RawDropColumnMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("ALTER TABLE \"Posts\" DROP COLUMN \"Summary\"");
    }

    private sealed class RawLowerCaseRenameMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("alter table \"Posts\"\n    rename column \"Summary\" to \"Excerpt\"");
    }

    private sealed class RawSetNotNullMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("ALTER TABLE \"Posts\" ALTER COLUMN \"Summary\" SET NOT NULL");
    }

    private sealed class RawAlterColumnTypeMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("ALTER TABLE \"Posts\" ALTER COLUMN \"Summary\" TYPE varchar(200)");
    }

    private sealed class RawBackfillMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"Posts\" ADD COLUMN IF NOT EXISTS \"Note\" text NULL");
            migrationBuilder.Sql("UPDATE \"Posts\" SET \"Note\" = \"Title\" WHERE \"Note\" IS NULL");
        }
    }

    [ContractMigration("Summary is read by no release still running")]
    private sealed class ContractedRawDropColumnMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("ALTER TABLE \"Posts\" DROP COLUMN \"Summary\"");
    }

    private sealed class NotNullWithoutDefaultMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.AddColumn<string>(name: "Slug2", table: "Posts", type: "text", nullable: false);
    }

    private sealed class NotNullWithDefaultsMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "Slug3", table: "Posts", type: "text", nullable: false, defaultValue: "");
            migrationBuilder.AddColumn<DateTime>(name: "Stamp", table: "Posts", type: "timestamp with time zone", nullable: false, defaultValueSql: "now()");
            migrationBuilder.AddColumn<string>(name: "Lower", table: "Posts", type: "text", nullable: false, computedColumnSql: "lower(\"Slug\")", stored: true);
        }
    }
}
