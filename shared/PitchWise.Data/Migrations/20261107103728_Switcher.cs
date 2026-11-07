using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PitchWise.Data.Migrations
{
    /// <inheritdoc />
    public partial class Switcher : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_switcher_program",
                table: "match_input",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "match_switcher",
                columns: table => new
                {
                    match_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    program_input_id = table.Column<Guid>(type: "uuid", nullable: false),
                    video_input_id = table.Column<Guid>(type: "uuid", nullable: false),
                    audio_input_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    warning = table.Column<string>(type: "text", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_retry_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    on_air_video_input_id = table.Column<Guid>(type: "uuid", nullable: true),
                    on_air_audio_input_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_match_switcher", x => x.match_id);
                    table.ForeignKey(
                        name: "FK_match_switcher_match_input_program_input_id",
                        column: x => x.program_input_id,
                        principalTable: "match_input",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_match_switcher_match_match_id",
                        column: x => x.match_id,
                        principalTable: "match",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "switcher_cut",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    match_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    input_id = table.Column<Guid>(type: "uuid", nullable: true),
                    origin = table.Column<string>(type: "text", nullable: false),
                    at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_switcher_cut", x => x.id);
                    table.ForeignKey(
                        name: "FK_switcher_cut_match_input_input_id",
                        column: x => x.input_id,
                        principalTable: "match_input",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_switcher_cut_match_match_id",
                        column: x => x.match_id,
                        principalTable: "match",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_match_switcher_enabled",
                table: "match_switcher",
                column: "enabled");

            migrationBuilder.CreateIndex(
                name: "IX_match_switcher_program_input_id",
                table: "match_switcher",
                column: "program_input_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_switcher_cut_input_id",
                table: "switcher_cut",
                column: "input_id");

            migrationBuilder.CreateIndex(
                name: "IX_switcher_cut_match_id_id",
                table: "switcher_cut",
                columns: new[] { "match_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "match_switcher");

            migrationBuilder.DropTable(
                name: "switcher_cut");

            migrationBuilder.DropColumn(
                name: "is_switcher_program",
                table: "match_input");
        }
    }
}
