using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlottR0._2.Repository.Migrations
{
    /// <inheritdoc />
    public partial class AddVoiceRecordings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VoiceRecordings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientRecordingId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TrialId = table.Column<Guid>(type: "uuid", nullable: true),
                    BlobPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    MimeType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ConfirmedPlot = table.Column<int>(type: "integer", nullable: true),
                    ConfirmedSubsample = table.Column<int>(type: "integer", nullable: true),
                    ConfirmedValue = table.Column<int>(type: "integer", nullable: true),
                    Transcript = table.Column<string>(type: "text", nullable: true),
                    ParsedPlot = table.Column<int>(type: "integer", nullable: true),
                    ParsedSubsample = table.Column<int>(type: "integer", nullable: true),
                    ParsedValue = table.Column<int>(type: "integer", nullable: true),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Received"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoiceRecordings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VoiceRecordings_Trials_TrialId",
                        column: x => x.TrialId,
                        principalTable: "Trials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VoiceRecordings_ClientRecordingId",
                table: "VoiceRecordings",
                column: "ClientRecordingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VoiceRecordings_TrialId",
                table: "VoiceRecordings",
                column: "TrialId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VoiceRecordings");
        }
    }
}
