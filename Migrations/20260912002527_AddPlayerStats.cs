using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripleTriadApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AvatarUrl",
                table: "Players",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Coins",
                table: "Players",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Experience",
                table: "Players",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Losses",
                table: "Players",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Ties",
                table: "Players",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Wins",
                table: "Players",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvatarUrl",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "Coins",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "Experience",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "Losses",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "Ties",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "Wins",
                table: "Players");
        }
    }
}
