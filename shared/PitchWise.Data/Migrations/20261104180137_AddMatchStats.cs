using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PitchWise.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "annotated_filename",
                table: "video",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "matchstats",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    video_id = table.Column<int>(type: "integer", nullable: false),
                    analysis_id = table.Column<int>(type: "integer", nullable: false),
                    possession_pct_a = table.Column<double>(type: "double precision", nullable: false),
                    possession_pct_b = table.Column<double>(type: "double precision", nullable: false),
                    controlled_seconds = table.Column<double>(type: "double precision", nullable: false),
                    loose_seconds = table.Column<double>(type: "double precision", nullable: false),
                    passes_a = table.Column<int>(type: "integer", nullable: false),
                    passes_b = table.Column<int>(type: "integer", nullable: false),
                    turnovers_a = table.Column<int>(type: "integer", nullable: false),
                    turnovers_b = table.Column<int>(type: "integer", nullable: false),
                    pass_accuracy_pct_a = table.Column<double>(type: "double precision", nullable: false),
                    pass_accuracy_pct_b = table.Column<double>(type: "double precision", nullable: false),
                    time_on_pitch_json = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_matchstats", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_matchstats_video_id",
                table: "matchstats",
                column: "video_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "matchstats");

            migrationBuilder.DropColumn(
                name: "annotated_filename",
                table: "video");
        }
    }
}
