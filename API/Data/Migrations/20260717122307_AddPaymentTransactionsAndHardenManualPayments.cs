using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentTransactionsAndHardenManualPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReversalReason",
                table: "Receipts",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReversedAt",
                table: "Receipts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Receipts",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Posted");

            migrationBuilder.AddColumn<string>(
                name: "AutoSuspendedForOverdueInvoiceId",
                table: "Licenses",
                type: "character varying(26)",
                maxLength: 26,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PaymentTransactions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                    InvoiceId = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                    Kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PaymentMethod = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PaymentReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ReceiptId = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: true),
                    ReversesTransactionId = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PerformedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentTransactions", x => x.Id);
                    table.CheckConstraint("CK_PaymentTransactions_AmountPositive", "\"Amount\" > 0");
                    table.CheckConstraint("CK_PaymentTransactions_PaymentShape", "(\"Kind\" = 'Payment' AND \"ReceiptId\" IS NOT NULL AND \"ReversesTransactionId\" IS NULL)\nOR (\"Kind\" = 'Reversal' AND \"ReceiptId\" IS NULL AND \"ReversesTransactionId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_PaymentTransactions_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentTransactions_PaymentTransactions_ReversesTransaction~",
                        column: x => x.ReversesTransactionId,
                        principalTable: "PaymentTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentTransactions_Receipts_ReceiptId",
                        column: x => x.ReceiptId,
                        principalTable: "Receipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql(
                """
                UPDATE "Receipts" SET "Status" = 'Posted' WHERE "Status" IS NULL OR "Status" = '';

                INSERT INTO "PaymentTransactions" (
                    "Id", "InvoiceId", "Kind", "Amount", "PaymentMethod", "PaymentReference", "Notes",
                    "ReceiptId", "ReversesTransactionId", "IdempotencyKey", "PerformedBy", "CreatedAt")
                SELECT
                    r."Id",
                    r."InvoiceId",
                    'Payment',
                    r."AmountPaid",
                    r."PaymentMethod",
                    r."PaymentReference",
                    r."Notes",
                    r."Id",
                    NULL,
                    'backfill:' || r."Id",
                    'system:backfill',
                    r."CreatedAt"
                FROM "Receipts" AS r
                WHERE NOT EXISTS (
                    SELECT 1 FROM "PaymentTransactions" AS p WHERE p."ReceiptId" = r."Id"
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_Status",
                table: "Receipts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Licenses_AutoSuspendedForOverdueInvoiceId",
                table: "Licenses",
                column: "AutoSuspendedForOverdueInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_InvoiceId",
                table: "PaymentTransactions",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_InvoiceId_IdempotencyKey",
                table: "PaymentTransactions",
                columns: new[] { "InvoiceId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_ReceiptId",
                table: "PaymentTransactions",
                column: "ReceiptId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransactions_ReversesTransactionId",
                table: "PaymentTransactions",
                column: "ReversesTransactionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentTransactions");

            migrationBuilder.DropIndex(
                name: "IX_Receipts_Status",
                table: "Receipts");

            migrationBuilder.DropIndex(
                name: "IX_Licenses_AutoSuspendedForOverdueInvoiceId",
                table: "Licenses");

            migrationBuilder.DropColumn(
                name: "ReversalReason",
                table: "Receipts");

            migrationBuilder.DropColumn(
                name: "ReversedAt",
                table: "Receipts");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Receipts");

            migrationBuilder.DropColumn(
                name: "AutoSuspendedForOverdueInvoiceId",
                table: "Licenses");
        }
    }
}
