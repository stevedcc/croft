using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Croft.Api.Migrations
{
    /// <inheritdoc />
    public partial class SocialReplies : Migration
    {
        /// <summary>
        /// Expand-only (AGENTS.md "Migrations", brief 12a): five nullable columns on Posts for the discussion links, and
        /// four new tables (threads, replies, blocks, avatars). Nothing the previous release reads changes.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BlueskyUri",
                table: "Posts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BlueskyUrl",
                table: "Posts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DiscussionLinkedAt",
                table: "Posts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MastodonStatusId",
                table: "Posts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MastodonUrl",
                table: "Posts",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SocialAvatars",
                columns: table => new
                {
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceUrl = table.Column<string>(type: "text", nullable: false),
                    StoragePath = table.Column<string>(type: "text", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialAvatars", x => x.Hash);
                });

            migrationBuilder.CreateTable(
                name: "SocialBlocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Platform = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Label = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialBlocks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SocialReplies",
                columns: table => new
                {
                    PostId = table.Column<Guid>(type: "uuid", nullable: false),
                    Platform = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ParentExternalId = table.Column<string>(type: "text", nullable: true),
                    AuthorHandle = table.Column<string>(type: "text", nullable: false),
                    AuthorDisplayName = table.Column<string>(type: "text", nullable: false),
                    AuthorUrl = table.Column<string>(type: "text", nullable: false),
                    AuthorAccountId = table.Column<string>(type: "text", nullable: false),
                    AvatarHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Permalink = table.Column<string>(type: "text", nullable: false),
                    ContentHtml = table.Column<string>(type: "text", nullable: false),
                    ContentWarning = table.Column<string>(type: "text", nullable: true),
                    HasMedia = table.Column<bool>(type: "boolean", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialReplies", x => new { x.PostId, x.Platform, x.ExternalId });
                    table.ForeignKey(
                        name: "FK_SocialReplies_Posts_PostId",
                        column: x => x.PostId,
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SocialThreads",
                columns: table => new
                {
                    PostId = table.Column<Guid>(type: "uuid", nullable: false),
                    Platform = table.Column<int>(type: "integer", nullable: false),
                    LastSuccessAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    NextDueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LeaseUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocialThreads", x => new { x.PostId, x.Platform });
                    table.ForeignKey(
                        name: "FK_SocialThreads_Posts_PostId",
                        column: x => x.PostId,
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SocialAvatars_SourceUrl",
                table: "SocialAvatars",
                column: "SourceUrl");

            migrationBuilder.CreateIndex(
                name: "IX_SocialBlocks_Platform_Kind_ExternalId",
                table: "SocialBlocks",
                columns: new[] { "Platform", "Kind", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocialReplies_AvatarHash",
                table: "SocialReplies",
                column: "AvatarHash");

            migrationBuilder.CreateIndex(
                name: "IX_SocialReplies_Platform_AuthorAccountId",
                table: "SocialReplies",
                columns: new[] { "Platform", "AuthorAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialReplies_Platform_ExternalId",
                table: "SocialReplies",
                columns: new[] { "Platform", "ExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_SocialThreads_NextDueAt",
                table: "SocialThreads",
                column: "NextDueAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SocialAvatars");

            migrationBuilder.DropTable(
                name: "SocialBlocks");

            migrationBuilder.DropTable(
                name: "SocialReplies");

            migrationBuilder.DropTable(
                name: "SocialThreads");

            migrationBuilder.DropColumn(
                name: "BlueskyUri",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "BlueskyUrl",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "DiscussionLinkedAt",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "MastodonStatusId",
                table: "Posts");

            migrationBuilder.DropColumn(
                name: "MastodonUrl",
                table: "Posts");
        }
    }
}
