using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoRenewalAndPayLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoRenewInvoice",
                table: "Licenses",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BillingAmount",
                table: "Licenses",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BillingAnchorDay",
                table: "Licenses",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingCurrency",
                table: "Licenses",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BillingPeriodMonths",
                table: "Licenses",
                type: "integer",
                nullable: false,
                defaultValue: 12);

            migrationBuilder.AddColumn<decimal>(
                name: "BillingTaxAmount",
                table: "Licenses",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "PayLinkExpiresAt",
                table: "Invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayLinkNonce",
                table: "Invoices",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RenewalAppliedAt",
                table: "Invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RenewalForExpiresAt",
                table: "Invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RenewalNewExpiresAt",
                table: "Invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RenewalPeriodMonths",
                table: "Invoices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RenewalPreviousExpiresAt",
                table: "Invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_OneRenewalPerPeriod",
                table: "Invoices",
                columns: new[] { "LicenseId", "RenewalForExpiresAt" },
                unique: true,
                filter: "\"RenewalForExpiresAt\" IS NOT NULL AND \"Status\" <> 'Void'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Invoices_OneRenewalPerPeriod",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "AutoRenewInvoice",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "BillingAmount",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "BillingAnchorDay",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "BillingCurrency",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "BillingPeriodMonths",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "BillingTaxAmount",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "PayLinkExpiresAt",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "PayLinkNonce",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "RenewalAppliedAt",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "RenewalForExpiresAt",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "RenewalNewExpiresAt",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "RenewalPeriodMonths",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "RenewalPreviousExpiresAt",
                table: "Invoices");
        }
    }
}
