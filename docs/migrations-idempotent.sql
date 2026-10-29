CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260519131319_InitialCreate') THEN
    CREATE TABLE "Customers" (
        "Id" character varying(26) NOT NULL,
        "Name" character varying(200) NOT NULL,
        "ContactEmail" character varying(320) NOT NULL,
        "ContactPhone" character varying(50),
        "InternalNotes" character varying(4000),
        "IsSuspended" boolean NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_Customers" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260519131319_InitialCreate') THEN
    CREATE TABLE "ServiceProducts" (
        "Id" character varying(26) NOT NULL,
        "Name" character varying(200) NOT NULL,
        "Code" character varying(50) NOT NULL,
        "Description" character varying(2000),
        "IsAvailableForSale" boolean NOT NULL,
        CONSTRAINT "PK_ServiceProducts" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260519131319_InitialCreate') THEN
    CREATE TABLE "IntegrationKeys" (
        "Id" character varying(26) NOT NULL,
        "ServiceProductId" character varying(26) NOT NULL,
        "KeyHash" character varying(100) NOT NULL,
        "IsActive" boolean NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "LastUsedAt" timestamp with time zone,
        CONSTRAINT "PK_IntegrationKeys" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_IntegrationKeys_ServiceProducts_ServiceProductId" FOREIGN KEY ("ServiceProductId") REFERENCES "ServiceProducts" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260519131319_InitialCreate') THEN
    CREATE TABLE "Licenses" (
        "Id" character varying(26) NOT NULL,
        "CustomerId" character varying(26) NOT NULL,
        "ServiceProductId" character varying(26) NOT NULL,
        "Status" character varying(50) NOT NULL,
        "ExpiresAt" timestamp with time zone,
        "PlanName" character varying(100) NOT NULL,
        "LicenseKeyHash" character varying(100),
        "LicenseKeySentAt" timestamp with time zone,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_Licenses" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_Licenses_Customers_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES "Customers" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_Licenses_ServiceProducts_ServiceProductId" FOREIGN KEY ("ServiceProductId") REFERENCES "ServiceProducts" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260519131319_InitialCreate') THEN
    CREATE TABLE "AuditLogs" (
        "Id" character varying(26) NOT NULL,
        "CustomerId" character varying(26),
        "LicenseId" character varying(26),
        "Action" character varying(100) NOT NULL,
        "PerformedBy" character varying(200) NOT NULL,
        "DetailsJson" jsonb,
        "IpAddress" character varying(45),
        "Timestamp" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_AuditLogs" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_AuditLogs_Customers_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES "Customers" ("Id") ON DELETE SET NULL,
        CONSTRAINT "FK_AuditLogs_Licenses_LicenseId" FOREIGN KEY ("LicenseId") REFERENCES "Licenses" ("Id") ON DELETE SET NULL
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260519131319_InitialCreate') THEN
    CREATE INDEX "IX_AuditLogs_CustomerId" ON "AuditLogs" ("CustomerId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260519131319_InitialCreate') THEN
    CREATE INDEX "IX_AuditLogs_LicenseId" ON "AuditLogs" ("LicenseId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260519131319_InitialCreate') THEN
    CREATE INDEX "IX_AuditLogs_Timestamp" ON "AuditLogs" ("Timestamp");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260519131319_InitialCreate') THEN
    CREATE INDEX "IX_Customers_ContactEmail" ON "Customers" ("ContactEmail");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260519131319_InitialCreate') THEN
    CREATE UNIQUE INDEX "IX_IntegrationKeys_ServiceProductId" ON "IntegrationKeys" ("ServiceProductId") WHERE "IsActive" = true;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260519131319_InitialCreate') THEN
    CREATE INDEX "IX_Licenses_CustomerId" ON "Licenses" ("CustomerId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260519131319_InitialCreate') THEN
    CREATE INDEX "IX_Licenses_CustomerId_ServiceProductId" ON "Licenses" ("CustomerId", "ServiceProductId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260519131319_InitialCreate') THEN
    CREATE INDEX "IX_Licenses_ServiceProductId" ON "Licenses" ("ServiceProductId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260519131319_InitialCreate') THEN
    CREATE UNIQUE INDEX "IX_ServiceProducts_Code" ON "ServiceProducts" ("Code");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260519131319_InitialCreate') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260519131319_InitialCreate', '9.0.16');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260521063711_AddInvoicesAndReceipts') THEN
    ALTER TABLE "AuditLogs" ADD "InvoiceId" character varying(26);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260521063711_AddInvoicesAndReceipts') THEN
    CREATE TABLE "Invoices" (
        "Id" character varying(26) NOT NULL,
        "CustomerId" character varying(26) NOT NULL,
        "LicenseId" character varying(26),
        "ServiceProductId" character varying(26),
        "InvoiceNumber" character varying(50) NOT NULL,
        "Status" character varying(50) NOT NULL,
        "IssueDate" timestamp with time zone NOT NULL,
        "DueDate" timestamp with time zone,
        "Currency" character varying(3) NOT NULL,
        "Subtotal" numeric(18,2) NOT NULL,
        "TaxAmount" numeric(18,2) NOT NULL,
        "TotalAmount" numeric(18,2) NOT NULL,
        "PlanName" character varying(100),
        "Description" character varying(2000),
        "InternalNotes" character varying(4000),
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_Invoices" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_Invoices_Customers_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES "Customers" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_Invoices_Licenses_LicenseId" FOREIGN KEY ("LicenseId") REFERENCES "Licenses" ("Id") ON DELETE SET NULL,
        CONSTRAINT "FK_Invoices_ServiceProducts_ServiceProductId" FOREIGN KEY ("ServiceProductId") REFERENCES "ServiceProducts" ("Id") ON DELETE SET NULL
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260521063711_AddInvoicesAndReceipts') THEN
    CREATE TABLE "Receipts" (
        "Id" character varying(26) NOT NULL,
        "InvoiceId" character varying(26) NOT NULL,
        "ReceiptNumber" character varying(50) NOT NULL,
        "AmountPaid" numeric(18,2) NOT NULL,
        "PaidAt" timestamp with time zone NOT NULL,
        "PaymentMethod" character varying(50) NOT NULL,
        "PaymentReference" character varying(200),
        "Notes" character varying(2000),
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_Receipts" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_Receipts_Invoices_InvoiceId" FOREIGN KEY ("InvoiceId") REFERENCES "Invoices" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260521063711_AddInvoicesAndReceipts') THEN
    CREATE INDEX "IX_AuditLogs_InvoiceId" ON "AuditLogs" ("InvoiceId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260521063711_AddInvoicesAndReceipts') THEN
    CREATE INDEX "IX_Invoices_CustomerId" ON "Invoices" ("CustomerId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260521063711_AddInvoicesAndReceipts') THEN
    CREATE UNIQUE INDEX "IX_Invoices_InvoiceNumber" ON "Invoices" ("InvoiceNumber");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260521063711_AddInvoicesAndReceipts') THEN
    CREATE INDEX "IX_Invoices_LicenseId" ON "Invoices" ("LicenseId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260521063711_AddInvoicesAndReceipts') THEN
    CREATE INDEX "IX_Invoices_ServiceProductId" ON "Invoices" ("ServiceProductId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260521063711_AddInvoicesAndReceipts') THEN
    CREATE INDEX "IX_Invoices_Status" ON "Invoices" ("Status");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260521063711_AddInvoicesAndReceipts') THEN
    CREATE INDEX "IX_Receipts_InvoiceId" ON "Receipts" ("InvoiceId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260521063711_AddInvoicesAndReceipts') THEN
    CREATE UNIQUE INDEX "IX_Receipts_ReceiptNumber" ON "Receipts" ("ReceiptNumber");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260521063711_AddInvoicesAndReceipts') THEN
    ALTER TABLE "AuditLogs" ADD CONSTRAINT "FK_AuditLogs_Invoices_InvoiceId" FOREIGN KEY ("InvoiceId") REFERENCES "Invoices" ("Id") ON DELETE SET NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260521063711_AddInvoicesAndReceipts') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260521063711_AddInvoicesAndReceipts', '9.0.16');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260522080128_AddIdentity') THEN
    CREATE TABLE "AspNetRoles" (
        "Id" text NOT NULL,
        "Name" character varying(256),
        "NormalizedName" character varying(256),
        "ConcurrencyStamp" text,
        CONSTRAINT "PK_AspNetRoles" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260522080128_AddIdentity') THEN
    CREATE TABLE "AspNetUsers" (
        "Id" text NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        "UserName" character varying(256),
        "NormalizedUserName" character varying(256),
        "Email" character varying(256),
        "NormalizedEmail" character varying(256),
        "EmailConfirmed" boolean NOT NULL,
        "PasswordHash" text,
        "SecurityStamp" text,
        "ConcurrencyStamp" text,
        "PhoneNumber" text,
        "PhoneNumberConfirmed" boolean NOT NULL,
        "TwoFactorEnabled" boolean NOT NULL,
        "LockoutEnd" timestamp with time zone,
        "LockoutEnabled" boolean NOT NULL,
        "AccessFailedCount" integer NOT NULL,
        CONSTRAINT "PK_AspNetUsers" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260522080128_AddIdentity') THEN
    CREATE TABLE "AspNetRoleClaims" (
        "Id" integer GENERATED BY DEFAULT AS IDENTITY,
        "RoleId" text NOT NULL,
        "ClaimType" text,
        "ClaimValue" text,
        CONSTRAINT "PK_AspNetRoleClaims" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_AspNetRoleClaims_AspNetRoles_RoleId" FOREIGN KEY ("RoleId") REFERENCES "AspNetRoles" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260522080128_AddIdentity') THEN
    CREATE TABLE "AspNetUserClaims" (
        "Id" integer GENERATED BY DEFAULT AS IDENTITY,
        "UserId" text NOT NULL,
        "ClaimType" text,
        "ClaimValue" text,
        CONSTRAINT "PK_AspNetUserClaims" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_AspNetUserClaims_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260522080128_AddIdentity') THEN
    CREATE TABLE "AspNetUserLogins" (
        "LoginProvider" text NOT NULL,
        "ProviderKey" text NOT NULL,
        "ProviderDisplayName" text,
        "UserId" text NOT NULL,
        CONSTRAINT "PK_AspNetUserLogins" PRIMARY KEY ("LoginProvider", "ProviderKey"),
        CONSTRAINT "FK_AspNetUserLogins_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260522080128_AddIdentity') THEN
    CREATE TABLE "AspNetUserRoles" (
        "UserId" text NOT NULL,
        "RoleId" text NOT NULL,
        CONSTRAINT "PK_AspNetUserRoles" PRIMARY KEY ("UserId", "RoleId"),
        CONSTRAINT "FK_AspNetUserRoles_AspNetRoles_RoleId" FOREIGN KEY ("RoleId") REFERENCES "AspNetRoles" ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_AspNetUserRoles_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260522080128_AddIdentity') THEN
    CREATE TABLE "AspNetUserTokens" (
        "UserId" text NOT NULL,
        "LoginProvider" text NOT NULL,
        "Name" text NOT NULL,
        "Value" text,
        CONSTRAINT "PK_AspNetUserTokens" PRIMARY KEY ("UserId", "LoginProvider", "Name"),
        CONSTRAINT "FK_AspNetUserTokens_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260522080128_AddIdentity') THEN
    CREATE INDEX "IX_AspNetRoleClaims_RoleId" ON "AspNetRoleClaims" ("RoleId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260522080128_AddIdentity') THEN
    CREATE UNIQUE INDEX "RoleNameIndex" ON "AspNetRoles" ("NormalizedName");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260522080128_AddIdentity') THEN
    CREATE INDEX "IX_AspNetUserClaims_UserId" ON "AspNetUserClaims" ("UserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260522080128_AddIdentity') THEN
    CREATE INDEX "IX_AspNetUserLogins_UserId" ON "AspNetUserLogins" ("UserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260522080128_AddIdentity') THEN
    CREATE INDEX "IX_AspNetUserRoles_RoleId" ON "AspNetUserRoles" ("RoleId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260522080128_AddIdentity') THEN
    CREATE INDEX "EmailIndex" ON "AspNetUsers" ("NormalizedEmail");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260522080128_AddIdentity') THEN
    CREATE UNIQUE INDEX "UserNameIndex" ON "AspNetUsers" ("NormalizedUserName");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260522080128_AddIdentity') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260522080128_AddIdentity', '9.0.16');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260526090515_AddKeyLookupHashes') THEN
    ALTER TABLE "Licenses" ADD "LicenseKeyLookupHash" character varying(64);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260526090515_AddKeyLookupHashes') THEN
    ALTER TABLE "IntegrationKeys" ADD "KeyLookupHash" character varying(64);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260526090515_AddKeyLookupHashes') THEN
    CREATE INDEX "IX_Licenses_ServiceProductId_LicenseKeyLookupHash" ON "Licenses" ("ServiceProductId", "LicenseKeyLookupHash");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260526090515_AddKeyLookupHashes') THEN
    CREATE INDEX "IX_IntegrationKeys_KeyLookupHash" ON "IntegrationKeys" ("KeyLookupHash");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260526090515_AddKeyLookupHashes') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260526090515_AddKeyLookupHashes', '9.0.16');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260716114636_AddInvoiceBrandProfile') THEN
    CREATE TABLE "InvoiceBrandProfiles" (
        "Id" character varying(26) NOT NULL,
        "CompanyName" character varying(200) NOT NULL,
        "AddressLine1" character varying(300),
        "AddressLine2" character varying(300),
        "Phone" character varying(50),
        "Website" character varying(300),
        "LogoBytes" bytea,
        "LogoContentType" character varying(100),
        "UpdatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_InvoiceBrandProfiles" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260716114636_AddInvoiceBrandProfile') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260716114636_AddInvoiceBrandProfile', '9.0.16');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260716120244_AddInvoiceBrandPaymentFields') THEN
    ALTER TABLE "InvoiceBrandProfiles" ADD "PaymentDetails" character varying(2000);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260716120244_AddInvoiceBrandPaymentFields') THEN
    ALTER TABLE "InvoiceBrandProfiles" ADD "PaymentMethods" character varying(500);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260716120244_AddInvoiceBrandPaymentFields') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260716120244_AddInvoiceBrandPaymentFields', '9.0.16');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260716151448_ReplaceInvoiceBrandPaymentFieldsWithOptions') THEN
    ALTER TABLE "InvoiceBrandProfiles" ADD "PaymentOptionsJson" text;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260716151448_ReplaceInvoiceBrandPaymentFieldsWithOptions') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260716151448_ReplaceInvoiceBrandPaymentFieldsWithOptions') THEN
    ALTER TABLE "InvoiceBrandProfiles" DROP COLUMN "PaymentDetails";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260716151448_ReplaceInvoiceBrandPaymentFieldsWithOptions') THEN
    ALTER TABLE "InvoiceBrandProfiles" DROP COLUMN "PaymentMethods";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260716151448_ReplaceInvoiceBrandPaymentFieldsWithOptions') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260716151448_ReplaceInvoiceBrandPaymentFieldsWithOptions', '9.0.16');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260717095233_AddHybridWorkflowAndEmailOutbox') THEN
    ALTER TABLE "Customers" ADD "BillingEmail" character varying(320);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260717095233_AddHybridWorkflowAndEmailOutbox') THEN
    ALTER TABLE "Customers" ADD "TechnicalEmail" character varying(320);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260717095233_AddHybridWorkflowAndEmailOutbox') THEN
    CREATE TABLE "EmailOutboxMessages" (
        "Id" character varying(26) NOT NULL,
        "Kind" character varying(50) NOT NULL,
        "Status" character varying(50) NOT NULL,
        "ToEmail" character varying(320) NOT NULL,
        "Subject" character varying(500) NOT NULL,
        "HtmlBody" text NOT NULL,
        "CustomerId" character varying(26),
        "LicenseId" character varying(26),
        "InvoiceId" character varying(26),
        "ReceiptId" character varying(26),
        "EncryptedPayload" bytea,
        "AttemptCount" integer NOT NULL,
        "NextAttemptAt" timestamp with time zone,
        "LastError" character varying(2000),
        "ProviderMessageId" character varying(500),
        "CreatedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone NOT NULL,
        "SentAt" timestamp with time zone,
        CONSTRAINT "PK_EmailOutboxMessages" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_EmailOutboxMessages_Customers_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES "Customers" ("Id") ON DELETE SET NULL,
        CONSTRAINT "FK_EmailOutboxMessages_Invoices_InvoiceId" FOREIGN KEY ("InvoiceId") REFERENCES "Invoices" ("Id") ON DELETE SET NULL,
        CONSTRAINT "FK_EmailOutboxMessages_Licenses_LicenseId" FOREIGN KEY ("LicenseId") REFERENCES "Licenses" ("Id") ON DELETE SET NULL,
        CONSTRAINT "FK_EmailOutboxMessages_Receipts_ReceiptId" FOREIGN KEY ("ReceiptId") REFERENCES "Receipts" ("Id") ON DELETE SET NULL
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260717095233_AddHybridWorkflowAndEmailOutbox') THEN
    CREATE INDEX "IX_EmailOutboxMessages_CustomerId" ON "EmailOutboxMessages" ("CustomerId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260717095233_AddHybridWorkflowAndEmailOutbox') THEN
    CREATE INDEX "IX_EmailOutboxMessages_InvoiceId" ON "EmailOutboxMessages" ("InvoiceId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260717095233_AddHybridWorkflowAndEmailOutbox') THEN
    CREATE INDEX "IX_EmailOutboxMessages_LicenseId" ON "EmailOutboxMessages" ("LicenseId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260717095233_AddHybridWorkflowAndEmailOutbox') THEN
    CREATE INDEX "IX_EmailOutboxMessages_ReceiptId" ON "EmailOutboxMessages" ("ReceiptId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260717095233_AddHybridWorkflowAndEmailOutbox') THEN
    CREATE INDEX "IX_EmailOutboxMessages_Status_NextAttemptAt" ON "EmailOutboxMessages" ("Status", "NextAttemptAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260717095233_AddHybridWorkflowAndEmailOutbox') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260717095233_AddHybridWorkflowAndEmailOutbox', '9.0.16');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260717122307_AddPaymentTransactionsAndHardenManualPayments') THEN
    ALTER TABLE "Receipts" ADD "ReversalReason" character varying(2000);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260717122307_AddPaymentTransactionsAndHardenManualPayments') THEN
    ALTER TABLE "Receipts" ADD "ReversedAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260717122307_AddPaymentTransactionsAndHardenManualPayments') THEN
    ALTER TABLE "Receipts" ADD "Status" character varying(50) NOT NULL DEFAULT 'Posted';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260717122307_AddPaymentTransactionsAndHardenManualPayments') THEN
    ALTER TABLE "Licenses" ADD "AutoSuspendedForOverdueInvoiceId" character varying(26);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260717122307_AddPaymentTransactionsAndHardenManualPayments') THEN
    CREATE TABLE "PaymentTransactions" (
        "Id" character varying(26) NOT NULL,
        "InvoiceId" character varying(26) NOT NULL,
        "Kind" character varying(50) NOT NULL,
        "Amount" numeric(18,2) NOT NULL,
        "PaymentMethod" character varying(50) NOT NULL,
        "PaymentReference" character varying(200),
        "Notes" character varying(2000),
        "ReceiptId" character varying(26),
        "ReversesTransactionId" character varying(26),
        "IdempotencyKey" character varying(100) NOT NULL,
        "PerformedBy" character varying(256) NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_PaymentTransactions" PRIMARY KEY ("Id"),
        CONSTRAINT "CK_PaymentTransactions_AmountPositive" CHECK ("Amount" > 0),
        CONSTRAINT "CK_PaymentTransactions_PaymentShape" CHECK (("Kind" = 'Payment' AND "ReceiptId" IS NOT NULL AND "ReversesTransactionId" IS NULL)
    OR ("Kind" = 'Reversal' AND "ReceiptId" IS NULL AND "ReversesTransactionId" IS NOT NULL)),
        CONSTRAINT "FK_PaymentTransactions_Invoices_InvoiceId" FOREIGN KEY ("InvoiceId") REFERENCES "Invoices" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_PaymentTransactions_PaymentTransactions_ReversesTransaction~" FOREIGN KEY ("ReversesTransactionId") REFERENCES "PaymentTransactions" ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_PaymentTransactions_Receipts_ReceiptId" FOREIGN KEY ("ReceiptId") REFERENCES "Receipts" ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260717122307_AddPaymentTransactionsAndHardenManualPayments') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260717122307_AddPaymentTransactionsAndHardenManualPayments') THEN
    CREATE INDEX "IX_Receipts_Status" ON "Receipts" ("Status");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260717122307_AddPaymentTransactionsAndHardenManualPayments') THEN
    CREATE INDEX "IX_Licenses_AutoSuspendedForOverdueInvoiceId" ON "Licenses" ("AutoSuspendedForOverdueInvoiceId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260717122307_AddPaymentTransactionsAndHardenManualPayments') THEN
    CREATE INDEX "IX_PaymentTransactions_InvoiceId" ON "PaymentTransactions" ("InvoiceId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260717122307_AddPaymentTransactionsAndHardenManualPayments') THEN
    CREATE UNIQUE INDEX "IX_PaymentTransactions_InvoiceId_IdempotencyKey" ON "PaymentTransactions" ("InvoiceId", "IdempotencyKey");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260717122307_AddPaymentTransactionsAndHardenManualPayments') THEN
    CREATE UNIQUE INDEX "IX_PaymentTransactions_ReceiptId" ON "PaymentTransactions" ("ReceiptId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260717122307_AddPaymentTransactionsAndHardenManualPayments') THEN
    CREATE UNIQUE INDEX "IX_PaymentTransactions_ReversesTransactionId" ON "PaymentTransactions" ("ReversesTransactionId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260717122307_AddPaymentTransactionsAndHardenManualPayments') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260717122307_AddPaymentTransactionsAndHardenManualPayments', '9.0.16');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260928122640_HardenLicenseBillingEdgeCases') THEN
    DROP INDEX "IX_Licenses_CustomerId_ServiceProductId";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260928122640_HardenLicenseBillingEdgeCases') THEN
    DROP INDEX "IX_Customers_ContactEmail";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260928122640_HardenLicenseBillingEdgeCases') THEN
    ALTER TABLE "Licenses" ADD "Version" bigint NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260928122640_HardenLicenseBillingEdgeCases') THEN
    UPDATE "Customers"
    SET "ContactEmail" = lower(btrim("ContactEmail"))
    WHERE "ContactEmail" IS NOT NULL
      AND "ContactEmail" <> lower(btrim("ContactEmail"));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260928122640_HardenLicenseBillingEdgeCases') THEN
    WITH ranked AS (
        SELECT "Id",
               "ContactEmail",
               ROW_NUMBER() OVER (
                   PARTITION BY "ContactEmail"
                   ORDER BY "CreatedAt", "Id") AS rn
        FROM "Customers"
    )
    UPDATE "Customers" AS c
    SET "ContactEmail" = CASE
        WHEN position('@' IN r."ContactEmail") > 1 THEN
            left(split_part(r."ContactEmail", '@', 1), 240)
            || '+dup-' || c."Id"
            || '@' || left(split_part(r."ContactEmail", '@', 2), 50)
        ELSE
            left(r."ContactEmail", 280) || '+dup-' || c."Id"
    END
    FROM ranked AS r
    WHERE c."Id" = r."Id"
      AND r.rn > 1;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260928122640_HardenLicenseBillingEdgeCases') THEN
    WITH ranked AS (
        SELECT "Id",
               ROW_NUMBER() OVER (
                   PARTITION BY "CustomerId", "ServiceProductId"
                   ORDER BY
                       CASE "Status"
                           WHEN 'Active' THEN 0
                           WHEN 'Suspended' THEN 1
                           WHEN 'Pending' THEN 2
                           WHEN 'Expired' THEN 3
                           ELSE 4
                       END,
                       "UpdatedAt" DESC,
                       "Id" DESC) AS rn
        FROM "Licenses"
        WHERE "Status" <> 'Revoked'
    )
    UPDATE "Licenses" AS l
    SET "Status" = 'Revoked',
        "UpdatedAt" = NOW() AT TIME ZONE 'UTC'
    FROM ranked AS r
    WHERE l."Id" = r."Id"
      AND r.rn > 1;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260928122640_HardenLicenseBillingEdgeCases') THEN
    CREATE UNIQUE INDEX "IX_Licenses_OneOpenPerCustomerService" ON "Licenses" ("CustomerId", "ServiceProductId") WHERE "Status" <> 'Revoked';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260928122640_HardenLicenseBillingEdgeCases') THEN
    CREATE UNIQUE INDEX "IX_Customers_ContactEmail" ON "Customers" ("ContactEmail");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260928122640_HardenLicenseBillingEdgeCases') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260928122640_HardenLicenseBillingEdgeCases', '9.0.16');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027123314_AddListAndScanIndexes') THEN
    CREATE INDEX "IX_Licenses_CreatedAt" ON "Licenses" ("CreatedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027123314_AddListAndScanIndexes') THEN
    CREATE INDEX "IX_Licenses_Status_ExpiresAt" ON "Licenses" ("Status", "ExpiresAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027123314_AddListAndScanIndexes') THEN
    CREATE INDEX "IX_Invoices_IssueDate" ON "Invoices" ("IssueDate");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027123314_AddListAndScanIndexes') THEN
    CREATE INDEX "IX_Invoices_Status_DueDate" ON "Invoices" ("Status", "DueDate");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027123314_AddListAndScanIndexes') THEN
    CREATE INDEX "IX_EmailOutboxMessages_CreatedAt" ON "EmailOutboxMessages" ("CreatedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027123314_AddListAndScanIndexes') THEN
    CREATE INDEX "IX_Customers_CreatedAt" ON "Customers" ("CreatedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261027123314_AddListAndScanIndexes') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261027123314_AddListAndScanIndexes', '9.0.16');
    END IF;
END $EF$;
COMMIT;

