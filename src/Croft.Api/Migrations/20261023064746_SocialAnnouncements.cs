using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Croft.Api.Migrations
{
    /// <inheritdoc />
    public partial class SocialAnnouncements : Migration
    {
        /// <summary>
        /// Expand-only (AGENTS.md "Migrations", brief 12d): three columns on Posts (the announcement text, null, and the two flags, not null with
        /// default false, so a pod of the previous release inserts posts that announce nothing) and the SocialAnnouncements outbox table. Nothing the previous
        /// release reads changes.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AnnounceBluesky",
                table: "Posts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AnnounceMastodon",
                table: "Posts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AnnouncementText",
                table: "Posts",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SocialAnnouncements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PostId = table.Column<Guid>(type: "uuid", nullable: false),
                    Platform = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    QueuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LeaseUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RemoteId = table.Column<string>(type: "text", nullable: true),
                    RemoteUrl = table.Column<string>(type: "text", nullable: true),
                    PostedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SentText = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialAnnouncements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocialAnnouncements_Posts_PostId",
                        column: x => x.PostId,
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Posts that are still to be published announce themselves when they are (Kind 0 = Post; Status 0 = Draft, 1 = Scheduled). Published posts and
            // pages keep the default, off: republishing an old post must not announce it by surprise. One statement over the few rows that qualify; it
            // changes nothing a public page shows, so there is no degraded interval to avoid (AGENTS.md "Rules for data migrations"). A draft created
            // through a pod of the previous release while it rolls out stays off (that pod inserts the default): the owner can switch it on.
            migrationBuilder.Sql("""UPDATE "Posts" SET "AnnounceMastodon" = true, "AnnounceBluesky" = true WHERE "Kind" = 0 AND "Status" IN (0, 1);""");

            migrationBuilder.CreateIndex(
                name: "IX_SocialAnnouncements_NextAttemptAt",
                table: "SocialAnnouncements",
                column: "NextAttemptAt");

            migrationBuilder.CreateIndex(
                name: "IX_SocialAnnouncements_PostId_Platform",
                table: "SocialAnnouncements",
                columns: new[] { "PostId", "Platform" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SocialAnnouncements");

            migrationBuilder.DropColumn(
                name: "AnnounceBluesky",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "AnnounceMastodon",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "AnnouncementText",
                table: "Posts");
        }
    }
}
