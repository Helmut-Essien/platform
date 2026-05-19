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

## Phase 2 — Admin authentication

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

**Acceptance**

- Login returns valid JWT; protected endpoint returns 401 without token, 200 with valid token.

---

## Phase 3 — Admin domain APIs + audit

**Goal:** Full admin CRUD for customers, catalog, licenses with audit trail.

**Scope**

- Controllers (or minimal APIs) with `[Authorize]`:
  - Customers: list, get, create, update, suspend/reactivate
  - ServiceProducts: list, get, create, update
  - Licenses: list (with `IgnoreQueryFilters` when needed), get, create (Pending), update plan/expiry
- Application services encapsulate business rules
- **AuditLog** written on every mutating action (`PerformedBy` from JWT `sub`/`name`, `IpAddress` from connection)
- DTOs in `Shared` for all request/response shapes
- FluentValidation or data annotations on DTOs

**License operations (status only — keys in Phase 4)**

- Issue → `Pending` or workflow as designed
- Suspend / revoke / renew metadata (expiry, plan) without email yet if split; prefer aligning with Phase 4 for key rotation on renew

**Acceptance**

- CRUD via HTTP + JWT; `AuditLogs` rows for each change; suspended customer visible in customer list; license list respects global filter unless endpoint documents `includeSuspended`.

---

## Phase 4 — License lifecycle, keys, and email

**Goal:** Complete license state machine with secure key delivery.

**Scope**

- Operations: **issue**, **activate**, **suspend**, **renew**, **revoke**
- On **Active** (new or renewed):
  - Generate cryptographically random key: `{SERVICE_CODE}-XXXX-YYYY`
  - `BCrypt.HashPassword` → `LicenseKeyHash`; set `LicenseKeySentAt`
  - Send plain key to `Customer.ContactEmail` via **SMTP** or **SendGrid** (`IOptions<EmailSettings>`, `IEmailSender`)
- Audit on every status transition
- Never persist or log plain license keys (except one-time email send)

**Config**

```json
"Email": { "Provider": "Smtp|SendGrid", "Host", "Port", "ApiKey", "FromAddress" }
```

**Acceptance**

- Activate issues key + email; DB has hash only; renew rotates key; revoke/suspend blocks validation later (Phase 5).

---

## Phase 5 — License validation + Redis

**Goal:** SaaS apps validate customer licenses at runtime.

**Scope**

- `POST /api/licenses/validate` — **no JWT**; auth via `X-Integration-Key` only
- Resolve integration key → `ServiceProduct`; BCrypt-verify header key
- BCrypt-verify body `licenseKey` against `LicenseKeyHash`
- Checks: status `Active`, not expired, customer not suspended, optional `serviceCode` match
- **Redis** (`docker-compose` service):
  - Deny-list: `license:{licenseId}` on revoke/suspend (and customer suspend)
  - Optional cache: valid validation result with short TTL
- Update `IntegrationKey.LastUsedAt` on success
- DTOs: `ValidateLicenseRequest`, `ValidateLicenseResponse` in `Shared`

**Response**

```json
{ "isValid": true, "planName": "Pro", "expiresAt": "2027-01-01T00:00:00Z" }
```

**Acceptance**

- Valid key + valid integration header → 200 `isValid: true`
- Wrong key, wrong integration key, revoked, expired, deny-listed → `isValid: false`
- Integration key from another product rejected

---

## Phase 6 — Blazor MudBlazor admin UI

**Goal:** Developer admin dashboard in `Client/`.

**Scope**

- NuGet: `MudBlazor`, project ref to `Shared`
- `RootNamespace`: `Platform.Client`
- Auth: login page, store JWT (local storage or auth state), `AuthorizationMessageHandler` on `HttpClient`
- Pages:
  - Dashboard (counts: customers, active licenses, expiring soon)
  - Customers grid + create/edit + suspend
  - Service catalog grid
  - Licenses grid + issue/suspend/renew/revoke actions + status badges (`MudChip`)
  - Audit log viewer (read-only grid, filters)
  - Integration keys management (rotate/revoke; show plain key once on create)
- API base URL in `wwwroot/appsettings.json` or `Client` config

**Acceptance**

- Admin can perform full lifecycle from UI; unauthorized routes redirect to login.

---

## Future infrastructure (not scheduled)

- `docker-compose`: add `redis` service when Phase 5 starts
- Production: managed Postgres, Redis, secrets vault, HTTPS, CORS for Client origin
- Rate limiting on `/api/licenses/validate`
- Health checks: `/health` (DB + Redis)
