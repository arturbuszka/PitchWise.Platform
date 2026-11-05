using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PitchWise.Data.Migrations
{
    /// <inheritdoc />
    public partial class MatchModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "match",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    public_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    program_input_id = table.Column<Guid>(type: "uuid", nullable: true),
                    analysis_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_match", x => x.id);
                    table.ForeignKey(
                        name: "FK_match_analysissession_analysis_id",
                        column: x => x.analysis_id,
                        principalTable: "analysissession",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_match_tenant_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenant",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "match_input",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    match_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    stream_key_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_match_input", x => x.id);
                    table.ForeignKey(
                        name: "FK_match_input_match_match_id",
                        column: x => x.match_id,
                        principalTable: "match",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_match_analysis_id",
                table: "match",
                column: "analysis_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_match_program_input_id",
                table: "match",
                column: "program_input_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_match_public_id",
                table: "match",
                column: "public_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_match_status",
                table: "match",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_match_tenant_id",
                table: "match",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_match_input_match_id",
                table: "match_input",
                column: "match_id");

            migrationBuilder.AddForeignKey(
                name: "FK_match_match_input_program_input_id",
                table: "match",
                column: "program_input_id",
                principalTable: "match_input",
                principalColumn: "id");

            // Each channel becomes a match with one input. The input keeps the channel's id and key
            // hash, so encoders that are already set up keep working. A channel that was live goes
            // to waiting, and the next poll puts it right.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    channel record;
                    new_analysis_id integer;
                BEGIN
                    FOR channel IN SELECT * FROM livestream LOOP
                        INSERT INTO analysissession (tenant_id, name, sport, status, created_at, updated_at)
                        VALUES (channel.tenant_id, channel.name, 'football', 'draft', channel.created_at, channel.created_at)
                        RETURNING id INTO new_analysis_id;

                        INSERT INTO match (id, tenant_id, name, public_id, status, analysis_id, created_at, started_at, ended_at)
                        VALUES (gen_random_uuid(), channel.tenant_id, channel.name, replace(gen_random_uuid()::text, '-', ''),
                                CASE WHEN channel.status = 'ended' THEN 'ended' ELSE 'waiting' END,
                                new_analysis_id, channel.created_at, channel.started_at, channel.ended_at);

                        INSERT INTO match_input (id, match_id, name, stream_key_hash, created_at)
                        SELECT channel.id, m.id, 'Main camera', channel.stream_key_hash, channel.created_at
                        FROM match m WHERE m.analysis_id = new_analysis_id;

                        UPDATE match SET program_input_id = channel.id WHERE analysis_id = new_analysis_id;
                    END LOOP;
                END $$;
                """);

            migrationBuilder.DropTable(
                name: "livestream");
        }

        // Going back leaves an empty livestream table: matches are not turned back into channels.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_match_match_input_program_input_id",
                table: "match");

            migrationBuilder.DropTable(
                name: "match_input");

            migrationBuilder.DropTable(
                name: "match");

            migrationBuilder.CreateTable(
                name: "livestream",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    stream_key_hash = table.Column<string>(type: "text", nullable: false),
                    tenant_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_livestream", x => x.id);
                    table.ForeignKey(
                        name: "FK_livestream_tenant_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenant",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_livestream_status",
                table: "livestream",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_livestream_tenant_id",
                table: "livestream",
                column: "tenant_id");
        }
    }
}
