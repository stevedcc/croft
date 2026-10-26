using Croft.Shared;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Croft.Api.Migrations;

/// <summary>
/// Bounds PageViews.Referrer (2048) and UserAgent (512), which were unbounded text. The API has cut
/// both to those lengths on write since 0.6.3, so a pod old enough to write a longer value no longer
/// runs; the trim below only repairs rows written before that. It runs in this migration's one
/// transaction: retention keeps about 90 days of page views and the trim has been live since 0.6.3,
/// so the UPDATEs touch few rows, and the type change holds an ACCESS EXCLUSIVE lock for one scan.
/// Postgres left() counts code points and the API cuts UTF-16 units (surrogate-safe), so a trimmed row
/// is never longer than what the API would have written.
/// </summary>
[ContractMigration("Referrer and UserAgent have been truncated to 2048/512 on write since 0.6.3; no pod older than that runs")]
public partial class _20261026134409_PageViewTextBounds : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""UPDATE "PageViews" SET "Referrer" = left("Referrer", 2048) WHERE length("Referrer") > 2048;""");
        migrationBuilder.Sql("""UPDATE "PageViews" SET "UserAgent" = left("UserAgent", 512) WHERE length("UserAgent") > 512;""");

        migrationBuilder.AlterColumn<string>(
            name: "UserAgent",
            table: "PageViews",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "text",
            oldNullable: true);

        migrationBuilder.AlterColumn<string>(
            name: "Referrer",
            table: "PageViews",
            type: "character varying(2048)",
            maxLength: 2048,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "text",
            oldNullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "UserAgent",
            table: "PageViews",
            type: "text",
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(512)",
            oldMaxLength: 512,
            oldNullable: true);

        migrationBuilder.AlterColumn<string>(
            name: "Referrer",
            table: "PageViews",
            type: "text",
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(2048)",
            oldMaxLength: 2048,
            oldNullable: true);
    }
}
