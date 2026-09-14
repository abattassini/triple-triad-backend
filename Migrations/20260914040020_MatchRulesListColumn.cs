using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripleTriadApi.Migrations
{
    /// <inheritdoc />
    public partial class MatchRulesListColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The Rules column changed from an integer bitmask to a comma separated list of rule
            // names ("" = no rules). Dropping and re-adding is used instead of ALTER COLUMN because
            // PostgreSQL cannot cast integer to text in place; this also applies cleanly to a
            // database where AddMatchRules was never run.
            migrationBuilder.DropColumn(name: "Rules", table: "Matches");

            migrationBuilder.AddColumn<string>(
                name: "Rules",
                table: "Matches",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Rules", table: "Matches");

            migrationBuilder.AddColumn<int>(
                name: "Rules",
                table: "Matches",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
