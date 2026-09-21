using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TripleTriadApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerPacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlayerPacks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlayerId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PackCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    FirstAcquiredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastAcquiredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerPacks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerPacks_PlayerId_PackCode",
                table: "PlayerPacks",
                columns: new[] { "PlayerId", "PackCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerPacks");
        }
    }
}
