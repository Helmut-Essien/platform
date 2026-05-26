using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddKeyLookupHashes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LicenseKeyLookupHash",
                table: "Licenses",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KeyLookupHash",
                table: "IntegrationKeys",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Licenses_ServiceProductId_LicenseKeyLookupHash",
                table: "Licenses",
                columns: new[] { "ServiceProductId", "LicenseKeyLookupHash" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationKeys_KeyLookupHash",
                table: "IntegrationKeys",
                column: "KeyLookupHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Licenses_ServiceProductId_LicenseKeyLookupHash",
                table: "Licenses");

            migrationBuilder.DropIndex(
                name: "IX_IntegrationKeys_KeyLookupHash",
                table: "IntegrationKeys");

            migrationBuilder.DropColumn(
                name: "LicenseKeyLookupHash",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "KeyLookupHash",
                table: "IntegrationKeys");
        }
    }
}
