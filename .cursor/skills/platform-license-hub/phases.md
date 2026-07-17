# Implementation phases

Work **one phase per delivery**. Include migrations, config, CLI commands, and next-step note. Wait for user confirmation before continuing.

---

## Phase 1 — Data foundation (DONE)

**Delivered**

- `Shared/Enums/LicenseStatus.cs`, `AuditAction.cs`
- `API/Entities/*` (5 entities, ULID IDs)
- `API/Data/EntityConfigurations/*`, `AppDbContext`, `SeedData`
- `docker-compose.yml` (Postgres 16)
- Migration `InitialCreate`
- `Program.cs`: `MigrateAsync` + seed on startup

**Acceptance**

- DB has 4 `ServiceProducts`, 1 demo `Customer`, 4 `IntegrationKeys` (hashed), 0 `Licenses`
- `dotnet build` succeeds

---

## Phase 2 — Admin authentication (DONE)

**Goal:** Secure admin-only API access for the future Blazor UI.

**Scope**

- ASP.NET Core **Identity** with EF stores on `AppDbContext` (or separate context if cleaner)
- `ApplicationUser` / roles (e.g. `Admin`)
- JWT issuance endpoint (e.g. `POST /api/auth/login`)
- `appsettings`: `Jwt:Issuer`, `Audience`, `Key`, expiry
- `[Authorize]` on admin controllers (stub or health check)
- Seed single admin user (config-driven password in Development; secrets in prod)
- DTOs in `Shared`: `LoginRequest`, `LoginResponse` (token + expiry)

**Suggested folders**

```
API/
  Identity/ or Entities/ApplicationUser.cs
  Services/AuthService.cs or ITokenService
  Controllers/AuthController.cs
Shared/
  Dtos/Auth/
```

**Out of scope:** Blazor login UI, customer/license CRUD, validation endpoint.

**Delivered**

- `ApplicationUser`, Identity tables (migration `AddIdentity`)
- `POST /api/auth/login`, `GET /api/auth/me`
- `[Authorize(Policy = AdminOnly)]` on Invoices, Licenses, Receipts controllers
- `JwtTokenService`, `IdentitySeedData` (dev admin in `appsettings.Development.json`)
- Audit `PerformedBy` from JWT claims via `AdminRequestContext`

**Acceptance**

- Login returns valid JWT; protected endpoint returns 401 without token, 200 with valid token.

---

## Phase 3 — Admin domain APIs + audit (DONE)

**Goal:** Full admin CRUD for customers, catalog, licenses with audit trail.

**Delivered**

- DTOs: `Shared/Dtos/Customers/`, `ServiceProducts/`, `Licenses/UpdateLicenseRequest`, `Audit/AuditLogDto`
- Services: `CustomerService`, `ServiceProductService`; `LicenseService` extended (list/get with `includeSuspendedCustomers`, update, suspend, revoke); `AuditLogService.ListAsync`
- Controllers: `CustomersController`, `ServiceProductsController`, `AuditLogsController` (`GET /api/audit-logs`), `LicensesController` extended
- Enum: `AuditAction.LicenseUpdated`, `ServiceProductCreated`, `ServiceProductUpdated`
- DI: `ICustomerService`, `IServiceProductService` in `Program.cs`

**Acceptance**

- CRUD via HTTP + JWT; `AuditLogs` rows for each change; suspended customer visible in customer list; license list respects global filter unless `?includeSuspendedCustomers=true`.

---

## Phase 4 — License lifecycle, keys, and email (DONE)

**Delivered**

- `ILicenseKeyDeliveryService` / `LicenseKeyDeliveryService` — `{CODE}-XXXX-YYYY` keys, BCrypt hash, email via `IEmailSender`
- `EmailSettings`, `SmtpEmailSender`, `LoggingEmailSender` (dev default)
- `LicenseService` activate/renew deliver keys; audit `LicenseKeyRotated` on renew

**Acceptance**

- Activate/renew store hash only; plain key emailed (or logged via Logging provider).

---

## Phase 5 — License validation + Redis (DONE)

**Delivered**

- `POST /api/licenses/validate` + `X-Integration-Key` (`LicenseValidationController`)
- `LicenseValidationService`, `RedisLicenseDenyListService`, Redis in `docker-compose.yml`
- Deny-list on license suspend/revoke and customer suspend
- DTOs: `ValidateLicenseRequest`, `ValidateLicenseResponse`
- `IntegrationKeysController` — list, create (plain key once), revoke

**Acceptance**

- Valid integration key + license key → `isValid: true`; denied/revoked/suspended → `isValid: false`.

---

## Phase 7 — Hybrid onboarding, durable email, lifecycle automation (DONE)

- Independent activation/renewal billing and delivery flags
- Customer billing/technical contacts with primary fallback
- PostgreSQL transactional email outbox; AES-GCM encrypted license-key payloads
- Background retries, delivery list/retry API, and admin Communications timeline
- Draft vs Create & Send invoices; explicit Rotate & Email Key semantics
- Expiry reminder worker, suspend/revoke notifications
- Optional auto-suspend of only the license linked to an overdue invoice

---

## Phase 3b — Invoices & receipts (DONE)

**Goal:** Billing records linked to customers and licenses.

**Delivered**

- Entities: `Invoice`, `Receipt`; `AuditLog.InvoiceId`
- Enums: `InvoiceStatus`, `PaymentMethod`; audit actions for billing
- Migration: `AddInvoicesAndReceipts`
- `BillingService`, `LicenseService` (activate/renew → invoice)
- Controllers: `InvoicesController`, `ReceiptsController`, `LicensesController`
- DTOs: `Shared/Dtos/Billing/`, `Shared/Dtos/Licenses/`
- Seed: demo invoice for Acme (`SeedBillingAsync`)

**Acceptance**

- Create invoice manually; record receipt updates status to Paid/PartiallyPaid
- Activate license creates invoice; void blocked when receipts exist

---

## Phase 6 — Blazor MudBlazor admin UI (DONE)

**Delivered**

- MudBlazor 7, Blazored.LocalStorage, JWT `AuthenticationStateProvider` + `JwtAuthorizationMessageHandler`
- Dark theme (`PlatformTheme`), pages: Dashboard, Customers, Services, Licenses, Invoices, Audit, Integration Keys, Validate tool, Login
- `wwwroot/appsettings.json` — `ApiBaseUrl`; API CORS for Client origin

**Acceptance**

- Login → navigate admin shell; CRUD flows call API with Bearer token.

---

## Future infrastructure (not scheduled)

- `docker-compose`: add `redis` service when Phase 5 starts
- Production: managed Postgres, Redis, secrets vault, HTTPS, CORS for Client origin
- Rate limiting on `/api/licenses/validate`
- Health checks: `/health` (DB + Redis)
