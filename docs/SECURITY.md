# Platform License Hub — Security & Operational Notes

This document records intentional security tradeoffs and operational behaviour for the admin hub and validation API.

## Admin authentication (Blazor WASM)

### JWT storage

The admin UI stores the JWT in **sessionStorage** (not localStorage). Session storage is cleared when the browser tab closes, which reduces exposure compared to persistent localStorage. It remains readable to JavaScript in the tab, so **XSS can still exfiltrate tokens**.

**Preferred production pattern:** HttpOnly, Secure, SameSite cookies issued by a **Backend-for-Frontend (BFF)** that holds tokens server-side. Pure Blazor WASM has no server component by default; migrating to a BFF is a future hardening step.

### JWT lifetime

Production default is **60 minutes** (`Jwt:ExpiryMinutes`). Development may use a longer value via `appsettings.Development.json`. Client auth state parses **role claims from the JWT** (it does not hardcode Admin).

### Password reset

Admins can recover access via:

- `POST /api/auth/forgot-password` — queues a reset link through the **email outbox** when the account exists (response is always generic to prevent account enumeration). The reset URL is stored only in the AES-GCM **EncryptedPayload** (`{{RESET_LINK}}` placeholder in HtmlBody), wiped after successful send or dead-letter.
- `POST /api/auth/reset-password` — applies a new password using the token from the email link.

Login, forgot-password, and reset-password endpoints share a **rate limit** (`RateLimiting:AuthLoginPermitLimit` per IP per window).

### Brute-force mitigation

`/api/auth/login` is rate-limited independently of the license validation endpoint. ASP.NET Identity lockout is enabled: **5** failed attempts → **15** minute lockout.

### Audit actor

`AdminRequestContext.GetPerformedBy` uses authenticated JWT claims only. Unauthenticated callers resolve to `"system"` — request headers are never trusted for audit identity.

## License keys

License keys are stored as **BCrypt hashes** only. Plaintext exists briefly in the encrypted email outbox until delivery succeeds. Candidate lookup uses the SHA-256 **lookup hash**; licenses without a lookup hash will not validate until the key is **rotated**.

**If a customer loses their key:** the plain key cannot be retrieved. Use **Rotate key** in the admin UI to issue a replacement and invalidate the old key. This is by design.

## License validation API

### Rate limiting

`POST /api/licenses/validate` has three limits: the global policy (client IP + integration key, partition keys are SHA-256 hashed so no raw key sits in limiter memory), a per-**license-key** limit (default 30/min, so one leaked key cannot be hammered from many IPs; counted per integration key + license key, so someone who knows only a customer's license key cannot use up the real product's budget with made-up integration keys) and a per-IP limit (default 300/min). Rejections are `429` with `Retry-After`. The client IP is the TCP peer unless `ForwardedHeaders__Enabled` is on with explicit known proxies (forward limit clamped to 1–5).

### Source of truth and failure handling

PostgreSQL is the only source of truth for validation; Redis, the deny list and the validation cache are no longer on this path, so a stale cache can never keep a revoked/rotated key valid. Each check is one indexed query (plus a write only for a new device or a throttled last-seen update). Integration keys are cached in memory for up to 60 s (hits only) and the cache is flushed when a key is created or revoked.

If the database (or anything in the infrastructure chain) fails or exceeds `ValidationTimeoutSeconds`, the API answers `503 service_unavailable` with `Retry-After`, never `isValid:false`. Products keep running on their last signed token until its grace ends.

### Signed license tokens

Valid answers include an ES256 compact JWS signed with `Licensing__TokenSigningKey` (P-256). Public keys are at `GET /api/licenses/keys` (JWKS, with extra keys for rotation). Tokens expire at `grace_until` (default 7 days, never past the license expiry + `ExpiryGraceHours`). If the key is missing in Production a loud error is logged and tokens are omitted; set `Licensing__RequireTokenSigning=true` to refuse to start instead. In Development a throw-away key is generated per process.

### Devices

Optional `deviceId` binds activations to devices. Only a SHA-256 hash of the id is stored (unique per license). New-device activation locks the license row (`SELECT … FOR UPDATE`) and re-counts, so concurrent activations cannot exceed `MaxDevices`. Activations, releases and limit changes are audited.

### Email match

`email` on a validation is optional. It is trimmed, Unicode-normalized (NFC), its domain converted to punycode (IDN) with any trailing dot dropped, and compared ordinal-ignore-case with the customer's billing and contact email; both comparisons always run. The answer is only `emailMatches:true` or `reason:"email_mismatch"`: the stored address is never returned, and a mismatch carries no license id, plan, expiry, device counts or token. It is checked after the key is verified and before status and device activation, so a key holder without the owner's email cannot learn the license status or consume a device slot. Guessing is bounded by the per-license-key rate limit; failed matches are logged and written to the audit log at most once per license per `Licensing:EmailMismatchAuditMinutes` (default 15, in-memory throttle, bounded to 10,000 licenses) so a flood cannot bloat `AuditLogs`. No email (typed or stored) is written to the audit row or the token. Inputs over 254 characters are `400 invalid_request`.

## Global query filters

Licenses, invoices, receipts, and payment transactions have EF global filters that hide rows for **suspended customers** from default queries.

- **Admin list APIs** use `IgnoreQueryFilters()` where a complete admin view is required (e.g. invoice list, dashboard stats, lifecycle workers).
- License list supports `includeSuspendedCustomers=true` for explicit opt-in.

## Auto-suspend on overdue invoices

When `Lifecycle:AutoSuspendOnOverdue` is enabled, `OverdueInvoiceLifecycleWorker` polls on `Lifecycle:OverduePollMinutes` (default **60 minutes**). An invoice can therefore remain overdue for up to one poll interval before linked licenses are auto-suspended. Reversing a payment that puts the invoice past due suspends the linked active license immediately. Zero-total invoices are not treated as overdue. Calendar dates (midnight) expire or become overdue at the end of that UTC day.

## Pagination

List endpoints clamp `pageSize` to **100** via `PagingHelper.MaxPageSize`.

## CORS (production)

In non-Development environments, the API **fails fast at startup** if no origins are configured. Set `Cors:Origins` in configuration or a comma-separated `CORS_ORIGINS` environment variable. Same-origin Docker hosting (API serves WASM from `wwwroot`) can rely on no cross-origin requests; still set origins if the UI is on another host.

### Production email checklist

1. Set `Email__Provider` to `Smtp` or `Resend` (not `Logging`).
2. Configure `Email__FromAddress` and provider credentials.
3. Set `Email__Outbox__EncryptionKey` (distinct from `JWT_KEY`).
4. Set `Auth__ClientBaseUrl` to the public admin UI URL (password-reset links).
5. Set `Cors__Origins` / `CORS_ORIGINS` when UI and API differ by origin.
6. Keep `Jwt__ExpiryMinutes` at or below 60 unless a shorter session is desired.

## Forwarded headers

`UseForwardedHeaders` trusts `X-Forwarded-For` / `X-Forwarded-Proto` with known networks/proxies cleared (typical for PaaS). Ensure only the platform ingress can set these headers; otherwise rate-limit and audit IPs can be spoofed.

## Errors

Domain failures map through `PlatformExceptionHandler` to JSON with both legacy `message` and Problem Details `title`/`detail`/`status` (`NotFoundException` → 404, `ConflictException` → 409, `InvalidOperationException` → 400).

## Testing

- Most unit tests use EF Core InMemory (fast, no relational semantics).
- `PaymentLedgerPostgresTests` and `QueryFilterPostgresTests` run against real PostgreSQL when available (`PLATFORM_TEST_DB` connection string).
- Rate limiter partition logic is covered by unit tests.
