using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TripleTriadApi.Migrations
{
    /// <inheritdoc />
    public partial class CardElementAsArray : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Convert the scalar Element column into a text[] array. Postgres needs
            // an explicit USING clause for the varchar -> text[] change; the legacy
            // sentinel 'none' maps to an empty array (no element).
            migrationBuilder.Sql(
                """
                ALTER TABLE "Cards"
                ALTER COLUMN "Element" TYPE text[]
                USING (
                    CASE
                        WHEN "Element" = 'none' THEN '{}'::text[]
                        ELSE ARRAY["Element"::text]
                    END
                );
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "Cards"
                ALTER COLUMN "Element" TYPE character varying(20)
                USING (COALESCE("Element"[1], 'none'));
                """
            );
        }
    }
}
