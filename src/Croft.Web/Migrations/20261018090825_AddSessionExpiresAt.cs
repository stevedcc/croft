using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Croft.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionExpiresAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SessionExpiresAt",
                table: "UserSessions",
                type: "timestamp with time zone",
                nullable: true);

            // Existing rows end their session at their access token's expiry plus the default
            // Auth:SessionLifetime (30 days; configuration is not available to a migration), so
            // nobody signed in at deployment time is purged by it.
            migrationBuilder.Sql(
                "UPDATE \"UserSessions\" SET \"SessionExpiresAt\" = \"ExpiresAt\" + INTERVAL '30 days'");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_SessionExpiresAt",
                table: "UserSessions",
                column: "SessionExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserSessions_SessionExpiresAt",
                table: "UserSessions");

            migrationBuilder.DropColumn(
                name: "SessionExpiresAt",
                table: "UserSessions");
        }
    }
}
