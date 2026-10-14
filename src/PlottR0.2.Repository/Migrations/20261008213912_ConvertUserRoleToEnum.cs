using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlottR0._2.Repository.Migrations
{
    /// <inheritdoc />
    public partial class ConvertUserRoleToEnum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing rows hold free-text roles (e.g. 'Agronomist', 'Administrator',
            // 'Field Technician') that are not valid UserRole member names. Clear them
            // BEFORE narrowing the column so EF never tries to materialise an
            // unparseable enum value. The owner confirmed all current rows are
            // disposable test data, so nulling is sanctioned. The column itself is
            // only narrowed — never dropped — so the FKs that reference Users
            // (RefreshToken.UserId, Trial.CreatedBy) are untouched.
            migrationBuilder.Sql("UPDATE \"Users\" SET \"Role\" = NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "Role",
                table: "Users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Role",
                table: "Users",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);
        }
    }
}
