using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripleTriadApi.Migrations
{
    /// <inheritdoc />
    public partial class AddDeckFolderToCardImages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Card artwork now lives in the per-deck folder `ff8-deck`, so the
            // relative path stored in Cards.Image must include that folder.
            // Idempotent: rows already carrying the prefix are left untouched.
            migrationBuilder.Sql(
                """
                UPDATE "Cards"
                SET "Image" = 'ff8-deck/' || "Image"
                WHERE "Image" NOT LIKE 'ff8-deck/%';
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "Cards"
                SET "Image" = REPLACE("Image", 'ff8-deck/', '')
                WHERE "Image" LIKE 'ff8-deck/%';
                """
            );
        }
    }
}
