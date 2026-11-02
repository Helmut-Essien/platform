using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTrialsAndComplimentaryLicenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsComplimentary",
                table: "Licenses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsTrial",
                table: "Licenses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "TrialReminderForEndsAt",
                table: "Licenses",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TrialStartedAt",
                table: "Licenses",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsTrialConversion",
                table: "Invoices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Licenses_Complimentary_CreatedAt",
                table: "Licenses",
                column: "CreatedAt",
                filter: "\"IsComplimentary\"");

            migrationBuilder.CreateIndex(
                name: "IX_Licenses_OneTrialPerCustomerService",
                table: "Licenses",
                columns: new[] { "CustomerId", "ServiceProductId" },
                unique: true,
                filter: "\"TrialStartedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Licenses_Trial_ExpiresAt",
                table: "Licenses",
                column: "ExpiresAt",
                filter: "\"IsTrial\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Licenses_Complimentary_CreatedAt",
                table: "Licenses");

            migrationBuilder.DropIndex(
                name: "IX_Licenses_OneTrialPerCustomerService",
                table: "Licenses");

            migrationBuilder.DropIndex(
                name: "IX_Licenses_Trial_ExpiresAt",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "IsComplimentary",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "IsTrial",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "TrialReminderForEndsAt",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "TrialStartedAt",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "IsTrialConversion",
                table: "Invoices");
        }
    }
}
