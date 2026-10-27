using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddListAndScanIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Licenses_CreatedAt",
                table: "Licenses",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Licenses_Status_ExpiresAt",
                table: "Licenses",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_IssueDate",
                table: "Invoices",
                column: "IssueDate");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_Status_DueDate",
                table: "Invoices",
                columns: new[] { "Status", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailOutboxMessages_CreatedAt",
                table: "EmailOutboxMessages",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_CreatedAt",
                table: "Customers",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Licenses_CreatedAt",
                table: "Licenses");

            migrationBuilder.DropIndex(
                name: "IX_Licenses_Status_ExpiresAt",
                table: "Licenses");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_IssueDate",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_Status_DueDate",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_EmailOutboxMessages_CreatedAt",
                table: "EmailOutboxMessages");

            migrationBuilder.DropIndex(
                name: "IX_Customers_CreatedAt",
                table: "Customers");
        }
    }
}
