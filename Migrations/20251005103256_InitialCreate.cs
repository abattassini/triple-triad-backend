using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TripleTriadApi.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Cards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Image = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TopValue = table.Column<int>(type: "integer", nullable: false),
                    RightValue = table.Column<int>(type: "integer", nullable: false),
                    BottomValue = table.Column<int>(type: "integer", nullable: false),
                    LeftValue = table.Column<int>(type: "integer", nullable: false),
                    Element = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cards", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Matches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Player1Id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Player2Id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CurrentPlayerTurn = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WinnerId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Player1Score = table.Column<int>(type: "integer", nullable: false),
                    Player2Score = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Matches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CardPlacements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MatchId = table.Column<int>(type: "integer", nullable: false),
                    CardId = table.Column<int>(type: "integer", nullable: false),
                    PlayerId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    X = table.Column<int>(type: "integer", nullable: false),
                    Y = table.Column<int>(type: "integer", nullable: false),
                    PlacedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Owner = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CardPlacements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CardPlacements_Cards_CardId",
                        column: x => x.CardId,
                        principalTable: "Cards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CardPlacements_Matches_MatchId",
                        column: x => x.MatchId,
                        principalTable: "Matches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlayerHands",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MatchId = table.Column<int>(type: "integer", nullable: false),
                    PlayerId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CardId = table.Column<int>(type: "integer", nullable: false),
                    IsUsed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerHands", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlayerHands_Cards_CardId",
                        column: x => x.CardId,
                        principalTable: "Cards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlayerHands_Matches_MatchId",
                        column: x => x.MatchId,
                        principalTable: "Matches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CardPlacements_CardId",
                table: "CardPlacements",
                column: "CardId");

            migrationBuilder.CreateIndex(
                name: "IX_CardPlacements_MatchId_X_Y",
                table: "CardPlacements",
                columns: new[] { "MatchId", "X", "Y" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Matches_Player1Id_Player2Id",
                table: "Matches",
                columns: new[] { "Player1Id", "Player2Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Matches_Status",
                table: "Matches",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerHands_CardId",
                table: "PlayerHands",
                column: "CardId");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerHands_MatchId_PlayerId_CardId",
                table: "PlayerHands",
                columns: new[] { "MatchId", "PlayerId", "CardId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CardPlacements");

            migrationBuilder.DropTable(
                name: "PlayerHands");

            migrationBuilder.DropTable(
                name: "Cards");

            migrationBuilder.DropTable(
                name: "Matches");
        }
    }
}
