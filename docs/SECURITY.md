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

`POST /api/licenses/validate` is rate-limited by **client IP and integration key** (composite partition). This reduces quota sharing behind NAT/CDN for distinct SaaS integrations on the same egress IP.

### Redis availability

Redis is an **acceleration layer** for validation caching and deny-list invalidation. PostgreSQL remains authoritative.

- `AbortOnConnectFail=false` allows the API to start when Redis is temporarily unavailable.
- When Redis read/write fails, validation **falls back to PostgreSQL** (license status, customer suspension, BCrypt verify).
- Positive validation cache hits always **re-check license status in PostgreSQL** (cheap PK lookup) so a missed deny-list write cannot serve revoked licenses for the cache TTL.
- Deny-list entries have **no TTL**; they are cleared on reactivate. Positive cache invalidation runs even when the deny write fails.
- During a Redis outage, positive validation results are not cached — expect higher database load until Redis recovers.

## Global query filters

Licenses, invoices, receipts, and payment transactions have EF global filters that hide rows for **suspended customers** from default queries.

- **Admin list APIs** use `IgnoreQueryFilters()` where a complete admin view is required (e.g. invoice list, dashboard stats, lifecycle workers).
- License list supports `includeSuspendedCustomers=true` for explicit opt-in.

## Auto-suspend on overdue invoices

When `Lifecycle:AutoSuspendOnOverdue` is enabled, `OverdueInvoiceLifecycleWorker` polls on `Lifecycle:OverduePollMinutes` (default **60 minutes**). An invoice can therefore remain overdue for up to one poll interval before linked licenses are auto-suspended. This is intentional grace, not real-time enforcement.

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
