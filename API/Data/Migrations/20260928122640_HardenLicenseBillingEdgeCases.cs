using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class HardenLicenseBillingEdgeCases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Licenses_CustomerId_ServiceProductId",
                table: "Licenses");

            migrationBuilder.DropIndex(
                name: "IX_Customers_ContactEmail",
                table: "Customers");

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "Licenses",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Normalize before the unique index so existing rows can migrate.
            migrationBuilder.Sql(
                """
                UPDATE "Customers"
                SET "ContactEmail" = lower(btrim("ContactEmail"))
                WHERE "ContactEmail" IS NOT NULL
                  AND "ContactEmail" <> lower(btrim("ContactEmail"));
                """);

            migrationBuilder.Sql(
                """
                WITH ranked AS (
                    SELECT "Id",
                           "ContactEmail",
                           ROW_NUMBER() OVER (
                               PARTITION BY "ContactEmail"
                               ORDER BY "CreatedAt", "Id") AS rn
                    FROM "Customers"
                )
                UPDATE "Customers" AS c
                SET "ContactEmail" = CASE
                    WHEN position('@' IN r."ContactEmail") > 1 THEN
                        left(split_part(r."ContactEmail", '@', 1), 240)
                        || '+dup-' || c."Id"
                        || '@' || left(split_part(r."ContactEmail", '@', 2), 50)
                    ELSE
                        left(r."ContactEmail", 280) || '+dup-' || c."Id"
                END
                FROM ranked AS r
                WHERE c."Id" = r."Id"
                  AND r.rn > 1;
                """);

            migrationBuilder.Sql(
                """
                WITH ranked AS (
                    SELECT "Id",
                           ROW_NUMBER() OVER (
                               PARTITION BY "CustomerId", "ServiceProductId"
                               ORDER BY
                                   CASE "Status"
                                       WHEN 'Active' THEN 0
                                       WHEN 'Suspended' THEN 1
                                       WHEN 'Pending' THEN 2
                                       WHEN 'Expired' THEN 3
                                       ELSE 4
                                   END,
                                   "UpdatedAt" DESC,
                                   "Id" DESC) AS rn
                    FROM "Licenses"
                    WHERE "Status" <> 'Revoked'
                )
                UPDATE "Licenses" AS l
                SET "Status" = 'Revoked',
                    "UpdatedAt" = NOW() AT TIME ZONE 'UTC'
                FROM ranked AS r
                WHERE l."Id" = r."Id"
                  AND r.rn > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Licenses_OneOpenPerCustomerService",
                table: "Licenses",
                columns: new[] { "CustomerId", "ServiceProductId" },
                unique: true,
                filter: "\"Status\" <> 'Revoked'");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_ContactEmail",
                table: "Customers",
                column: "ContactEmail",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Licenses_OneOpenPerCustomerService",
                table: "Licenses");

            migrationBuilder.DropIndex(
                name: "IX_Customers_ContactEmail",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Licenses");

            migrationBuilder.CreateIndex(
                name: "IX_Licenses_CustomerId_ServiceProductId",
                table: "Licenses",
                columns: new[] { "CustomerId", "ServiceProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_Customers_ContactEmail",
                table: "Customers",
                column: "ContactEmail");
        }
    }
}
