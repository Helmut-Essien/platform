using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BrandContactEmailAndTaxId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContactEmail",
                table: "InvoiceBrandProfiles",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxId",
                table: "InvoiceBrandProfiles",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            // The new contact email starts as the business email; the admin can change or clear it in Settings.
            migrationBuilder.Sql(
                "UPDATE \"InvoiceBrandProfiles\" SET \"ContactEmail\" = 'helmutessien@gmail.com' WHERE \"ContactEmail\" IS NULL;");
            // A profile still on the old auto-seeded placeholder name gets the HelmutCode identity; only empty
            // fields are filled, so anything the admin already typed is kept.
            migrationBuilder.Sql(@"
UPDATE ""InvoiceBrandProfiles"" SET
    ""CompanyName"" = 'HelmutCode Solutions',
    ""AddressLine1"" = COALESCE(""AddressLine1"", 'YN13 Flask St'),
    ""AddressLine2"" = COALESCE(""AddressLine2"", 'Accra, Ghana'),
    ""Phone"" = COALESCE(""Phone"", '+233500785153'),
    ""Website"" = COALESCE(""Website"", 'helmutcode.runasp.net')
WHERE ""CompanyName"" = 'Platform License Hub';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContactEmail",
                table: "InvoiceBrandProfiles");

            migrationBuilder.DropColumn(
                name: "TaxId",
                table: "InvoiceBrandProfiles");
        }
    }
}
