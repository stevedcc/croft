using Croft.Shared;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Croft.Api.Migrations;

/// <summary>
/// Step two of dropping Posts.ViewCount and Posts.ShowInNavigation (brief 18c, issue 15). Release 0.7.4
/// (brief 17c) stopped mapping both columns and gave ViewCount a default; no pod older than that runs
/// any more, so nothing reads or writes them. The view totals live in PostViewCounts (20261019175325, which
/// copied them), the navigation flag in NavigationPlacement (20261018114827). The scaffolder cannot
/// generate this, since the snapshot no longer knows the columns, so the operations are written by hand.
/// </summary>
[ContractMigration("Unmapped since 0.7.4 (brief 17c); production runs only 0.7.4 or newer, so no pod reads or writes either column")]
public partial class _20261027151335_DropRetiredPostColumns : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ViewCount", table: "Posts");
        migrationBuilder.DropColumn(name: "ShowInNavigation", table: "Posts");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(name: "ViewCount", table: "Posts", type: "bigint", nullable: false, defaultValue: 0L);
        migrationBuilder.AddColumn<bool>(name: "ShowInNavigation", table: "Posts", type: "boolean", nullable: false, defaultValue: false);
    }
}
