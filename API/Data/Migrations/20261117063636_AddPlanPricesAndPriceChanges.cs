using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanPricesAndPriceChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DefaultAmount",
                table: "ProductPlans",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultCurrency",
                table: "ProductPlans",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DefaultPeriodMonths",
                table: "ProductPlans",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DefaultTaxAmount",
                table: "ProductPlans",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "LicensePriceChanges",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                    LicenseId = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                    BatchId = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                    PlanCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OldAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    OldTaxAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    OldCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    OldPeriodMonths = table.Column<int>(type: "integer", nullable: false),
                    NewAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    NewTaxAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    NewCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    NewPeriodMonths = table.Column<int>(type: "integer", nullable: true),
                    NoticeQueuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AppliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AppliedInvoiceId = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CancelReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LicensePriceChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LicensePriceChanges_Licenses_LicenseId",
                        column: x => x.LicenseId,
                        principalTable: "Licenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LicensePriceChanges_LicenseId_CreatedAt",
                table: "LicensePriceChanges",
                columns: new[] { "LicenseId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LicensePriceChanges_NoticeDue",
                table: "LicensePriceChanges",
                column: "EffectiveFrom",
                filter: "\"Status\" = 'Pending' AND \"NoticeQueuedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LicensePriceChanges_OnePendingPerLicense",
                table: "LicensePriceChanges",
                column: "LicenseId",
                unique: true,
                filter: "\"Status\" = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LicensePriceChanges");

            migrationBuilder.DropColumn(
                name: "DefaultAmount",
                table: "ProductPlans");

            migrationBuilder.DropColumn(
                name: "DefaultCurrency",
                table: "ProductPlans");

            migrationBuilder.DropColumn(
                name: "DefaultPeriodMonths",
                table: "ProductPlans");

            migrationBuilder.DropColumn(
                name: "DefaultTaxAmount",
                table: "ProductPlans");
        }
    }
}
