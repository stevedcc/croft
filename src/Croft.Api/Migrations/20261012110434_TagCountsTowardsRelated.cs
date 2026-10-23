using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Croft.Api.Migrations
{
    /// <inheritdoc />
    public partial class TagCountsTowardsRelated : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CountsTowardsRelated",
                table: "Tags",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CountsTowardsRelated",
                table: "Tags");
        }
    }
}
