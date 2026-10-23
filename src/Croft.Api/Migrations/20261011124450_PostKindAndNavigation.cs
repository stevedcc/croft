using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Croft.Api.Migrations
{
    /// <inheritdoc />
    public partial class PostKindAndNavigation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "Posts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "NavigationOrder",
                table: "Posts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "ShowInNavigation",
                table: "Posts",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "NavigationOrder",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "ShowInNavigation",
                table: "Posts");
        }
    }
}
