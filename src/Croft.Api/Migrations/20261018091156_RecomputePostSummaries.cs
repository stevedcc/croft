using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Croft.Api.Migrations
{
    /// <summary>
    /// Data only: clears every stored summary so PostSummaryBackfillService recomputes them at the
    /// next start with the sentence-end rule (a cut at a full stop no longer gets an ellipsis
    /// appended). Summary is nullable and the previous release already treats null as "not yet
    /// computed" (it shows an empty summary until the backfill fills it), so this is safe to run
    /// while old instances still serve. Down has nothing to restore: the backfill derives the
    /// column from the markdown.
    /// </summary>
    public partial class RecomputePostSummaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE \"Posts\" SET \"Summary\" = NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
