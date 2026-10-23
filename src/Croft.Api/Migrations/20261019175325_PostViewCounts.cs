using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Croft.Api.Migrations
{
    /// <inheritdoc />
    public partial class PostViewCounts : Migration
    {
        /// <summary>
        /// Expand-only (AGENTS.md "Migrations"): the view totals move to a narrow table of their own so
        /// counting a view stops rewriting the wide, heavily indexed Posts row. "Posts"."ViewCount" stays:
        /// a pod of the release before brief 13c (0.6.3 and older) still updates it during the rolling deploy, and a later contract migration
        /// drops it (see "Pending contracts"). The copy below carries the totals as they are when this
        /// migration runs; a view that an old pod counts after that lands only in the old column and is
        /// not in the new table. That small loss during one rollout is accepted: it can be at most the
        /// views the old pods count between the migration and their replacement, a count nobody uses
        /// for decisions. Posts with no counted views get no row (a missing row reads as 0).
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PostViewCounts",
                columns: table => new
                {
                    PostId = table.Column<Guid>(type: "uuid", nullable: false),
                    ViewCount = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostViewCounts", x => x.PostId);
                    table.ForeignKey(
                        name: "FK_PostViewCounts_Posts_PostId",
                        column: x => x.PostId,
                        principalTable: "Posts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO "PostViewCounts" ("PostId", "ViewCount")
                SELECT "Id", "ViewCount" FROM "Posts" WHERE "ViewCount" > 0
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PostViewCounts");
        }
    }
}
