using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlottR0._2.Repository.Migrations
{
    /// <inheritdoc />
    public partial class AddTrialStatusAndWorkbook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceWorkbookPath",
                table: "Trials",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Trials",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Created");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceWorkbookPath",
                table: "Trials");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Trials");
        }
    }
}
