using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Croft.Api.Migrations;

/// <summary>
/// Step one of dropping Posts.ViewCount and Posts.ShowInNavigation (brief 17c, issue 15). This release
/// no longer maps either column, so its INSERTs leave them out: ViewCount is bigint NOT NULL with no
/// default and gets one here (ShowInNavigation already defaults to false). The columns themselves stay
/// for the pods of the previous release, which still read and write them while a deploy rolls.
/// Step two, one release later, drops both with a hand-written [ContractMigration] DropColumn. The
/// scaffolder will not generate it, since the snapshot no longer knows the columns. For the same reason
/// the DropColumn/AddColumn operations the scaffold produced here were deleted by hand.
/// </summary>
public partial class _20261026133825_RetiredPostColumnsDefault : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""ALTER TABLE "Posts" ALTER COLUMN "ViewCount" SET DEFAULT 0;""");

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("""ALTER TABLE "Posts" ALTER COLUMN "ViewCount" DROP DEFAULT;""");
}
