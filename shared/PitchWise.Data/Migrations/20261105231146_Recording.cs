using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PitchWise.Data.Migrations
{
    /// <inheritdoc />
    public partial class Recording : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "gaps",
                table: "video",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "match_input_id",
                table: "video",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "recording_error",
                table: "match",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "recording_status",
                table: "match",
                type: "text",
                nullable: false,
                defaultValue: "none");

            // Every video so far is an upload, which has no gaps.
            migrationBuilder.Sql("UPDATE video SET gaps = '[]'");

            migrationBuilder.CreateIndex(
                name: "IX_video_match_input_id",
                table: "video",
                column: "match_input_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_video_match_input_match_input_id",
                table: "video",
                column: "match_input_id",
                principalTable: "match_input",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_video_match_input_match_input_id",
                table: "video");

            migrationBuilder.DropIndex(
                name: "IX_video_match_input_id",
                table: "video");

            migrationBuilder.DropColumn(
                name: "gaps",
                table: "video");

            migrationBuilder.DropColumn(
                name: "match_input_id",
                table: "video");

            migrationBuilder.DropColumn(
                name: "recording_error",
                table: "match");

            migrationBuilder.DropColumn(
                name: "recording_status",
                table: "match");
        }
    }
}
