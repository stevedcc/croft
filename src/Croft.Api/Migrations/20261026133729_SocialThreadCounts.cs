using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Croft.Api.Migrations;

/// <inheritdoc />
public partial class _20261026133729_SocialThreadCounts : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "CountsAt",
            table: "SocialThreads",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "LikeCount",
            table: "SocialThreads",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "ReplyCount",
            table: "SocialThreads",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "RepostCount",
            table: "SocialThreads",
            type: "integer",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CountsAt",
            table: "SocialThreads");

        migrationBuilder.DropColumn(
            name: "LikeCount",
            table: "SocialThreads");

        migrationBuilder.DropColumn(
            name: "ReplyCount",
            table: "SocialThreads");

        migrationBuilder.DropColumn(
            name: "RepostCount",
            table: "SocialThreads");
    }
}
