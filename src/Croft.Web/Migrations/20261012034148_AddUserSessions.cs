using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Croft.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddUserSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserSessions",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Sid = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Sub = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AccessToken = table.Column<string>(type: "text", nullable: false),
                    RefreshToken = table.Column<string>(type: "text", nullable: true),
                    IdToken = table.Column<string>(type: "text", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSessions", x => x.SessionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_ExpiresAt",
                table: "UserSessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_Sid",
                table: "UserSessions",
                column: "Sid");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_Sub",
                table: "UserSessions",
                column: "Sub");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserSessions");
        }
    }
}
