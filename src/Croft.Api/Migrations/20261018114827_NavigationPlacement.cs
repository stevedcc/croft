using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Croft.Api.Migrations
{
    /// <summary>
    /// Adds NavigationPlacement (None, Sidebar or Footer, stored as a string) and fills it from the
    /// ShowInNavigation flag: true becomes Sidebar, false stays None. Expand-only: ShowInNavigation
    /// stays, because API pods of the previous release still read and write it while the new ones
    /// roll in. A later contract migration drops it (AGENTS.md, "Pending contracts").
    /// </summary>
    public partial class NavigationPlacement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NavigationPlacement",
                table: "Posts",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.Sql(
                "UPDATE \"Posts\" SET \"NavigationPlacement\" = 'Sidebar' WHERE \"ShowInNavigation\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE \"Posts\" SET \"ShowInNavigation\" = (\"NavigationPlacement\" = 'Sidebar');");

            migrationBuilder.DropColumn(
                name: "NavigationPlacement",
                table: "Posts");
        }
    }
}
