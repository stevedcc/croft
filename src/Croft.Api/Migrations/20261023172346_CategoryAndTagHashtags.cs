using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Croft.Api.Migrations
{
    /// <inheritdoc />
    public partial class CategoryAndTagHashtags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Hashtags",
                table: "Tags",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Hashtags",
                table: "Categories",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Hashtags",
                table: "Tags");

            migrationBuilder.DropColumn(
                name: "Hashtags",
                table: "Categories");
        }
    }
}
