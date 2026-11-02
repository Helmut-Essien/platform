using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLicenseActivationInvoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ActivationAppliedAt",
                table: "Invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ActivationPeriodMonths",
                table: "Invoices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsActivationInvoice",
                table: "Invoices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_OneActivationPerLicense",
                table: "Invoices",
                column: "LicenseId",
                unique: true,
                filter: "\"IsActivationInvoice\" AND \"Status\" <> 'Void'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Invoices_OneActivationPerLicense",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ActivationAppliedAt",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ActivationPeriodMonths",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "IsActivationInvoice",
                table: "Invoices");
        }
    }
}
