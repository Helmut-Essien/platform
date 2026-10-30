using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PaymentCheckoutConcurrencyAndCap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LiveMode",
                table: "PaymentCheckouts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "PaidAmountMinor",
                table: "PaymentCheckouts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                table: "PaymentCheckouts",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LiveMode",
                table: "PaymentCheckouts");

            migrationBuilder.DropColumn(
                name: "PaidAmountMinor",
                table: "PaymentCheckouts");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "PaymentCheckouts");
        }
    }
}
