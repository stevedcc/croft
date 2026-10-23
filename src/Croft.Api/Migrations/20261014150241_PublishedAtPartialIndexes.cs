using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Croft.Api.Migrations
{
    // WORKAROUND(W-033, EF Core): hand-written partial indexes (EF configuration would churn them); the filters hard-code the PostStatus/PostKind integers
    /// <summary>
    /// Two partial indexes over Posts."PublishedAt", written by hand instead of from a
    /// configuration class so both definitions stay together with the enum values their filters
    /// encode and no future migrations add run touches them. Filters use the enum's stored
    /// integer values (PostStatus/PostKind map to int): 1 = Scheduled, 2 = Published, 0 = Post.
    /// The first serves the scheduler's due-post polls and its earliest-scheduled read; the
    /// second serves the public list's PublishedAt sort.
    /// </summary>
    public partial class PublishedAtPartialIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Posts_Scheduled_PublishedAt",
                table: "Posts",
                column: "PublishedAt",
                filter: "\"Status\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Posts_Published_PublishedAt",
                table: "Posts",
                column: "PublishedAt",
                descending: new[] { true },
                filter: "\"Status\" = 2 AND \"Kind\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Posts_Scheduled_PublishedAt",
                table: "Posts");

            migrationBuilder.DropIndex(
                name: "IX_Posts_Published_PublishedAt",
                table: "Posts");
        }
    }
    // END WORKAROUND(W-033)
}
