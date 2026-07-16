using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceInvoiceBrandPaymentFieldsWithOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PaymentOptionsJson",
                table: "InvoiceBrandProfiles",
                type: "text",
                nullable: true);

            // Preserve any existing flat payment fields as a single structured option.
            migrationBuilder.Sql("""
                UPDATE "InvoiceBrandProfiles"
                SET "PaymentOptionsJson" = CASE
                    WHEN "PaymentMethods" IS NOT NULL AND BTRIM("PaymentMethods") <> '' THEN
                        json_build_array(
                            json_build_object(
                                'method', BTRIM("PaymentMethods"),
                                'details', NULLIF(BTRIM(COALESCE("PaymentDetails", '')), '')
                            )
                        )::text
                    WHEN "PaymentDetails" IS NOT NULL AND BTRIM("PaymentDetails") <> '' THEN
                        json_build_array(
                            json_build_object(
                                'method', 'Payment',
                                'details', BTRIM("PaymentDetails")
                            )
                        )::text
                    ELSE NULL
                END;
                """);

            migrationBuilder.DropColumn(
                name: "PaymentDetails",
                table: "InvoiceBrandProfiles");

            migrationBuilder.DropColumn(
                name: "PaymentMethods",
                table: "InvoiceBrandProfiles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PaymentDetails",
                table: "InvoiceBrandProfiles",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentMethods",
                table: "InvoiceBrandProfiles",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "InvoiceBrandProfiles"
                SET
                    "PaymentMethods" = LEFT(
                        COALESCE(("PaymentOptionsJson"::jsonb -> 0 ->> 'method'), NULL),
                        500),
                    "PaymentDetails" = LEFT(
                        COALESCE(("PaymentOptionsJson"::jsonb -> 0 ->> 'details'), NULL),
                        2000)
                WHERE "PaymentOptionsJson" IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "PaymentOptionsJson",
                table: "InvoiceBrandProfiles");
        }
    }
}
