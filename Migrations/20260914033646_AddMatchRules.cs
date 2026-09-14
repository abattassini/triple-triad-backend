using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripleTriadApi.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Rules",
                table: "Matches",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Rules",
                table: "Matches");
        }
    }
}
