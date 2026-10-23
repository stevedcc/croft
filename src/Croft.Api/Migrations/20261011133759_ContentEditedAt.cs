using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Croft.Api.Migrations
{
    /// <inheritdoc />
    public partial class ContentEditedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ContentEditedAt",
                table: "Posts",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc));

            // Existing rows have no content-edit history: their best record of it is UpdatedAt,
            // so the "Updated" dates shown for old content start from what the readers already
            // know rather than from the epoch.
            migrationBuilder.Sql("""UPDATE "Posts" SET "ContentEditedAt" = "UpdatedAt";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContentEditedAt",
                table: "Posts");
        }
    }
}
