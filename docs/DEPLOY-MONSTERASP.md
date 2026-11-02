# Deploying to MonsterASP.NET (Windows / IIS, .NET 9)

The API serves the Blazor WASM admin UI from its own `wwwroot` (same origin), so one site = one deploy.
IIS runs it **in-process**; the SDK generates `web.config` on publish (`hostingModel="inprocess"`), so no
custom `web.config` is committed. `ASPNETCORE_ENVIRONMENT` defaults to `Production` when unset.

> **PostgreSQL on MonsterASP is in beta.** Take regular backups (panel backups if offered, plus your own
> `pg_dump` on a schedule) and test a restore before relying on it.

## 1. Environment variables

Set these in the MonsterASP panel (website → environment variables / app settings). Never commit them.
`__` (double underscore) maps to a nested config key.

| Variable | Required | Value |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | **yes** | `Host=<pg-host>;Port=5432;Database=<db>;Username=<user>;Password=<pw>;SSL Mode=Prefer` (a `postgres://` URL in `DATABASE_URL` also works) |
| `Jwt__Key` | **yes** | Random secret, ≥ 32 chars (`openssl rand -base64 48`). Startup fails on placeholders/dev keys. |
| `Email__Outbox__EncryptionKey` | **yes** | Separate random secret, 32+ chars or 32-byte base64. Don't reuse `Jwt__Key`. |
| `Auth__ClientBaseUrl` | **yes** | Public site URL, e.g. `https://platform.example.com` (used in email links; default is localhost). |
| `AdminSeed__Email` / `AdminSeed__Password` | first start | Bootstrap admin (password: 8+ chars, upper, lower, digit). Remove the password afterwards. |
| `Email__Provider` | yes for real mail | `Resend` or `Smtp` (default `Logging` sends nothing). |
| `Email__FromAddress`, `Email__FromName` | with real mail | Sender identity. |
| `Email__ResendApiKey` | if Resend | Resend API key. |
| `Email__Host`, `Email__Port`, `Email__EnableSsl`, `Email__Username`, `Email__Password` | if Smtp | SMTP server settings (port default 587, SSL default true). |
| `Redis__ConnectionString` (or `REDIS_URL`) | no | Leave unset on MonsterASP: in-memory cache + deny lists are used (single instance only; a warning is logged). |
| `Database__MigrateOnStartup` | no | Default `false` in Production. Set `true` only if you want the app to migrate itself. |
| `Https__Redirect` | no | Default `true` in Production (with HSTS). |
| `CORS_ORIGINS` | no | Comma-separated extra origins only if a UI on another domain calls this API. |
| `ForwardedHeaders__Enabled` (+ `__KnownProxies__0` / `__KnownNetworks__0`) | no | Keep off for IIS in-process. Only enable behind a reverse proxy you list explicitly. |
| `Licensing__TokenSigningKey` | recommended | ECDSA P-256 private key (PKCS#8 PEM; `\n` escapes allowed) used to sign offline license tokens. Unset in Production = no tokens and a loud startup error log. See §11. |
| `Licensing__TokenSigningKeyId` | no | Key id (`kid`). Default = RFC 7638 thumbprint of the key. |
| `Licensing__AdditionalPublicKeys__0__Kid` / `__PublicKeyPem` | during rotation | Old public keys still published at `/api/licenses/keys` so tokens signed before a rotation keep verifying. |
| `Licensing__RequireTokenSigning` | no | `true` = refuse to start without a signing key (default `false`). |
| `Licensing__OfflineGraceDays` | no | How long a product may run offline on its last token (default `7`). Never extends past license expiry + `ExpiryGraceHours`. |
| `Licensing__ExpiryGraceHours` | no | Small allowance past the license expiry for the offline window (default `0`). |
| `Licensing__TokenRefreshHours` | no | When products should re-check (`refreshAfter`, default `12`, capped at half the grace window). |
| `Licensing__DefaultMaxDevices` | no | Device limit for newly issued licenses (default `3`; `0` = unlimited). Existing licenses stay unlimited until edited. |
| `Licensing__RequireDeviceId` | no | `true` = reject validations without `deviceId` (default `false` for old clients). |
| `Licensing__LicenseKeyPermitLimit` / `__PerIpPermitLimit` / `__RateLimitWindowSeconds` | no | Validation limits per license key (default 30) and per client IP (default 300) per window (default 60 s). |
| `Licensing__ValidationTimeoutSeconds` / `__UnavailableRetryAfterSeconds` | no | DB timeout before answering 503 (default 10) and the `Retry-After` sent with it (default 30). |
| `Paystack__SecretKey` | no | Enables online payments (`sk_test_…` or `sk_live_…`). Unset = feature hidden, endpoints return 503. See §9. |
| `Paystack__PublicKey` | no | `pk_test_…` / `pk_live_…`. Stored for completeness; checkout uses Paystack's hosted page. |
| `Paystack__CollectionCap` | no | Starter Business limit in GHS for the dashboard card (default `30000`; `0` hides it). |
| `Billing__RenewalInvoicesEnabled` | no | Automatic renewal invoices on/off (default `true`). See §10. |
| `Billing__RenewalInvoiceDaysBefore` | no | Days before expiry the renewal invoice is created and emailed (default `14`, 1–90). |
| `Billing__RenewalExtendFrom` | no | `Auto` (default), `PreviousExpiry` or `PaymentDate` — basis for the extension when a renewal invoice is paid. |
| `Billing__RenewalGraceDays` | no | `Auto` keeps the old expiry as basis unless it lapsed more than this many days (default `7`); also the due-date grace for already-lapsed licenses. |
| `Billing__PayLinkEnabled` | no | Public `/pay/{token}` links in invoice emails/PDFs (default `true`; needs Paystack). |
| `Billing__PayLinkValidDays` | no | Pay-link lifetime, re-extended when the invoice is re-sent (default `60`, 7–365). |
| `Billing__PayLinkSigningKey` | no | Dedicated 32+ char secret for pay-link signatures. Unset = derived from `Jwt__Key` (rotating either invalidates links already sent). |
| `Billing__TrialMonths` | no | Length of a free trial in months (default `1`, 1–12). See §10. |
| `Billing__TrialReminderDaysBefore` | no | Days before a trial ends that the reminder (with the first invoice, if priced) goes out (default `7`, 1–60). |
| `Billing__TrialMaxTotalMonths` | no | Longest a trial can run, counting extensions, from its start (default `3`, at least `TrialMonths`, max 24). |

## 2. Create the site and database

1. Panel → **Websites → Create**, pick **.NET 9** (EU/Germany datacenter is closest to Ghana).
2. Panel → **Databases → PostgreSQL (beta)** → create a database and user; note host, port, db, user, password.
3. Add the environment variables above.
4. Download the **Web Deploy** publish settings (server, site name, user, password).

## 3. Apply database migrations (before first start)

Migrations do **not** run on startup in Production. Either:

- From your machine: `dotnet ef database update -p API -s API --connection "<connection string>"`, or
- Run the idempotent script `docs/migrations-idempotent.sql` with `psql` or the panel's SQL tool. It is
  safe to run repeatedly. Regenerate after adding migrations:
  `dotnet ef migrations script --idempotent -p API -s API -o docs/migrations-idempotent.sql`

## 4. Publish

```bash
dotnet publish API/API.csproj -c Release
```

The output contains `wwwroot/_framework` (the Blazor client); `appsettings.Development.json` is excluded.

**Web Deploy:** create `API/Properties/PublishProfiles/MonsterASP.pubxml` (`*.pubxml` is git-ignored, keep it local):

```xml
<Project>
  <PropertyGroup>
    <WebPublishMethod>MSDeploy</WebPublishMethod>
    <MSDeployServiceURL>YOUR_SERVER_FROM_PANEL</MSDeployServiceURL>
    <DeployIisAppPath>YOUR_SITE_NAME</DeployIisAppPath>
    <UserName>YOUR_DEPLOY_USER</UserName>
    <AuthType>Basic</AuthType>
    <AllowUntrustedCertificate>true</AllowUntrustedCertificate>
    <SkipExtraFilesOnServer>true</SkipExtraFilesOnServer>
    <EnableMSDeployAppOffline>true</EnableMSDeployAppOffline>
    <LaunchSiteAfterPublish>false</LaunchSiteAfterPublish>
  </PropertyGroup>
</Project>
```

Then from Windows / Visual Studio (Web Deploy needs Windows' msdeploy): Publish → import the panel's
profile, or `dotnet publish API/API.csproj -c Release /p:PublishProfile=MonsterASP /p:Password=<deploy password>`.
From Linux, upload the `bin/Release/net9.0/publish/` folder by FTP instead.

## 4b. Deploy with GitHub Actions (`.github/workflows/deploy-monsterasp.yml`)

Runs on every push to `master` and on manual **Run workflow**. Windows runner: restore → build → test
(Postgres tests return early, `PLATFORM_TEST_DB` is not set) → `dotnet publish API/API.csproj -c Release -o publish`
→ write app settings into the published `web.config` as `<environmentVariables>` (plus
`ASPNETCORE_ENVIRONMENT=Production`) → optional migrations → `msdeploy.exe` sync with AppOffline → `/health` check
on `https://helmutcode.runasp.net` (falls back to http).

Repository secrets (Settings → Secrets and variables → Actions):

- Web Deploy: `MONSTER_SERVER` (bare host like `siteXXXX.siteasp.net`, `host:port`, or full
  `https://host:8172/msdeploy.axd` URL; port defaults to 8172), `MONSTER_SITE`, `MONSTER_USER`, `MONSTER_PASSWORD`.
- Required app settings (the job fails if missing): `ConnectionStrings__DefaultConnection`, `Jwt__Key`,
  `Email__Outbox__EncryptionKey`, `Auth__ClientBaseUrl`.
- Optional (skipped when empty): `AdminSeed__Email`, `AdminSeed__Password`, `Email__Provider`, `Email__FromAddress`,
  `Email__FromName`, `Email__ResendApiKey`, `Email__Host`, `Email__Port`, `Email__EnableSsl`, `Email__Username`,
  `Email__Password`, `Redis__ConnectionString`, `Database__MigrateOnStartup`, `Https__Redirect`, `CORS_ORIGINS`,
  `ForwardedHeaders__Enabled`, `Paystack__SecretKey`, `Paystack__PublicKey`, `Paystack__CollectionCap`.

Because the workflow writes these into `web.config`, settings in the panel for the same names are overridden by
each deploy. Secret values are never printed; msdeploy output is masked.

**Migrations:** every deploy applies `docs/migrations-idempotent.sql` before publishing; a failure stops the deploy and the old version keeps running. Optional secret `MIGRATIONS_CONNECTION` overrides the connection used. It runs
`docs/migrations-idempotent.sql` with `psql` using `ConnectionStrings__DefaultConnection` (key=value or `postgres://`
form; values must not contain `;`). The MonsterASP Postgres must accept connections from GitHub runners.

## 5. HTTPS

Panel → SSL → enable the free Let's Encrypt certificate for your (sub)domain. The app enables HSTS and
redirects HTTP→HTTPS in Production.

## 6. Keep-alive

IIS stops idle apps, which pauses the email outbox, overdue-invoice and expiry-reminder workers.
Add a panel **scheduled task** (or an uptime pinger) hitting `https://<site>/health` every 5 minutes.
`/health` is anonymous and not rate-limited; `/health/db` also checks the database (503 if unreachable).

## 7. First admin user

1. Set `AdminSeed__Email` and `AdminSeed__Password`, start/restart the site, sign in at `https://<site>/`.
2. Remove `AdminSeed__Password` from the panel afterwards (an existing user's password is never overwritten).

Production starts with an **empty catalogue**: demo products, demo customer/invoice and the hard-coded
integration keys are seeded only in Development. Create service products and integration keys in the UI.

## 8. Notes

- Run a single instance (in-memory cache, background workers and Postgres advisory locks assume it).
- Back up the beta Postgres regularly.
- Logs: set `stdoutLogEnabled="true"` in the deployed `web.config` temporarily if startup fails.

## 9. Online payments (Paystack)

Admins can open a Paystack hosted checkout (card or mobile money) for the outstanding balance of any unpaid
**GHS** invoice (**Pay with Paystack** on the invoice page). Paid checkouts are verified with Paystack's API and recorded
as normal receipts (idempotency key `paystack:{reference}`, so a payment is never recorded twice), which also emails
the receipt and reactivates suspended licenses as usual.

**Test mode first**

1. Paystack dashboard → Settings → API Keys & Webhooks (Test mode): copy the test secret and public keys.
2. Add GitHub secrets `Paystack__SecretKey` (`sk_test_…`) and optionally `Paystack__PublicKey` (`pk_test_…`) and
   redeploy. The startup log says `Online payments: Paystack enabled (test mode)`.
3. Set the **Test Webhook URL** to `https://helmutcode.runasp.net/api/payments/paystack/webhook`.
   Callback URLs are sent per transaction (`Auth__ClientBaseUrl` + `/invoices/{id}?payment=return`), so
   `Auth__ClientBaseUrl` must be the public site URL.
4. Pay a test invoice with Paystack's test card / test mobile money numbers (see Paystack docs → *Test payments*).

**How confirmation works**

- Webhook `charge.success` → signature checked (HMAC-SHA512 of the raw body with the secret key; 401 if invalid)
  → transaction verified with `GET /transaction/verify/{reference}` (status, amount in pesewas, currency) → receipt.
- Returning to the invoice page polls the same verification. A background worker re-verifies pending checkouts
  older than 2 minutes (every 5 minutes) and expires unpaid ones after 24 hours, so a missed webhook still
  gets recorded — keep the `/health` keep-alive (§6) running.
- If Paystack's amount or currency doesn't match, or the invoice was voided/paid in the meantime, nothing is recorded
  and the checkout is marked *NeedsReview* (look it up in the Paystack dashboard and refund or record manually).
- If you pass Paystack fees to the customer, the invoice is credited with the invoice amount (`requested_amount`);
  the dashboard total shows what Paystack actually charged. Test-mode payments never count toward the live total.
- The customer needs a valid billing or contact email (Paystack requires one); otherwise the button shows an error.
- Clicking **Pay with Paystack** again within an hour reuses the same checkout; older or different-amount
  checkouts stay open until verified and expire after 24 hours, so a late payment on them is still picked up.
- Webhook rate limit: 120 requests/minute per client IP (IIS in-process sees the real IP; with
  `ForwardedHeaders__Enabled` only trusted proxies are honoured).

**Going live**

1. Complete Paystack account activation (Starter Business: ID, TIN, settlement account in your name).
2. Replace `Paystack__SecretKey` (and `Paystack__PublicKey`) with the `sk_live_…` / `pk_live_…` keys and redeploy.
3. Set the **Live Webhook URL** to the same `https://helmutcode.runasp.net/api/payments/paystack/webhook`.
4. Make one small real payment and check the receipt appears. Watch the dashboard card against the
   GHS 30,000 starter limit and upgrade the account (business registration) before reaching it.

Never put the secret key in `appsettings*.json` or the client; it is only read from configuration and never logged.

## Review notes (Oct 2026)

- **Migration script is generated in CI.** The workflow runs `dotnet ef migrations script --idempotent` and applies that output. It fails the build if `docs/migrations-idempotent.sql` is stale; regenerate it with `dotnet ef migrations script --idempotent -p API -s API -o docs/migrations-idempotent.sql` and commit.
- **psql lookup:** `PGBIN`, then `PATH`, then `C:\Program Files\PostgreSQL\*\bin`. The job fails with a clear message if none is found.
- **Data Protection keys** are stored in the `DataProtectionKeys` table (migration `AddDataProtectionKeys`), so restarts/app-pool recycles do not invalidate protected tokens (e.g. password-reset links).
- **HTTPS redirect:** `Https:Redirect` defaults to true outside Development. If the site loops between http/https behind MonsterASP's proxy, set `Https__Redirect=false` (and/or `ForwardedHeaders__Enabled=true`).
- **Auth__ClientBaseUrl** must be the public site URL; the API logs a warning at startup if it is empty or points at localhost.
- **Secrets with commas:** msdeploy cannot parse commas in `MONSTER_SITE`/`MONSTER_USER`/`MONSTER_PASSWORD`; the job fails fast if one does.
- The `logs` folder is hidden from HTTP via IIS `requestFiltering/hiddenSegments`.

### 9.x Refunds, disputes, mode switches and backoff

- **Refunds and disputes.** `refund.processed` marks the checkout *Refunded*, and `charge.dispute.create`/`remind` mark it *Disputed*. The receipt is **not** reversed automatically. Review the invoice and reverse it by hand if needed. Flagged checkouts still count toward the monthly cap. Enable these events on the Paystack webhook.
- **Test/live domain.** A webhook whose `domain` doesn't match the configured key mode is ignored.
- **Key switch.** After switching from `sk_test_` to `sk_live_` (or back), pending checkouts from the other mode are expired without calling Paystack. Tell customers to start a new payment.
- **Backoff.** Reconcile stops a run after 3 consecutive Paystack failures (for example HTTP 429 or an outage) and retries on the next interval.
- **Client.** Each confirmation check times out after 10 s, and polling stops after about 30 s. Refreshing the page re-checks.


## 10. Automatic renewals and pay links

**What happens**

1. Give a license a *Recurring billing* price (Issue / Edit / Renew dialogs: price, tax, currency, period, "send renewal invoices automatically").
   Existing licenses have no price after the migration, so nothing is billed until you set one.
2. `RenewalInvoiceWorker` (hourly, `Billing__RenewalPollMinutes`, min 5) finds Active/Expired licenses of non-suspended customers with a
   price, auto-invoice on, and an expiry within `RenewalInvoiceDaysBefore` days (or lapsed ≤ `RenewalLookbackDays`, default 30), with no
   renewal invoice yet for that expiry. It creates one `Sent` invoice per license and period (due on the expiry date; lapsed licenses get
   today + grace days) and emails it with a **Pay online** button; the PDF carries the same link.
   One indexed query per 100 licenses; an advisory lock plus the unique index `IX_Invoices_OneRenewalPerPeriod` make overlapping runs
   (IIS overlapped recycle) safe.
3. When that invoice is fully paid (manual receipt or Paystack), the license is extended by one period in the same transaction:
   Expired → Active, a manual suspension stays, month-end dates keep their anniversary day (Jan 31 → Feb 28/29 → Mar 31).
   If the license was already renewed by hand, the payment does not extend it again (audit: *Renewal skipped*).
   **Revoke** voids the license's unpaid open invoices and kills their pay links. Invoices with a payment, or a Paystack checkout
   started within `Billing__VoidBlockAfterCheckoutMinutes`, stay open and are listed in the audit entry; a payment that still
   arrives is recorded but does not change the revoked license.
4. Reversing the payment restores the old expiry only if nothing else changed it; otherwise it is left and flagged
   (audit: *Renewal reversal needs review*).
5. A voided renewal invoice is **not** re-created automatically. Use Licenses → ⋮ → *Create renewal invoice* to issue it again.

**Pay links**: `/pay/{token}` needs no login, shows only invoice number, customer name, amounts and due date, and opens Paystack for the
balance. Tokens are HMAC-signed; void kills the link, a paid link stays readable for 1 day; invalid links all return the same 404,
expired ones 410; `api/public/pay/*` is rate limited (40/min per IP) and `no-store`. With Paystack off the page says online payment is
unavailable. Invoice page → *Copy pay link* gives the same URL.

**Manual test (Paystack test keys)**

1. Edit a test license: expiry within 14 days, price 1.00 GHS, yearly, auto on. Wait for the worker (≤ 1 h after start, 30 s after a
   restart) or use *Create renewal invoice*.
2. Check the email (Email deliveries) and PDF for the pay link; open it in a private window — no login prompt.
3. Pay with Paystack test card `4084 0840 8408 4081`; the page confirms, the license expiry moves one year, a renewal confirmation is queued.
4. Reopen the link the next day → 404/“no longer payable”; void another renewal invoice → its link returns 404 immediately.
5. Reverse the receipt → expiry restored (audit shows *Renewal reverted*).

### Free trials

- **Issue**: Licenses → *Issue License* → type *Free trial*. It is Active at once, the key is emailed, and it ends after
  `Billing__TrialMonths`. Set a price on it if the customer should continue paid; leave it empty for a plain trial.
- **Reminder** `Billing__TrialReminderDaysBefore` days before the end:
  - *With a price* (and automatic invoices on): the renewal worker sends the first invoice as the reminder — email
    "Your … trial ends on …", PDF attached, **Pay online** link. Paying converts the trial to a normal license; the paid
    period starts at the trial end (or at the payment date if paid more than `RenewalGraceDays` after it), same rules as renewals.
  - *Without a price* (or automatic invoices off): `LicenseExpiryReminderWorker` sends a plain "trial ends soon" email. The normal
    expiry reminders are not sent for trials, so the customer never gets two different messages.
  - One reminder per trial end date; extending the trial re-arms it.
- **Unpaid at the end**: the trial expires like any license (key stops validating). Paying the invoice later reactivates it.
- **One trial per customer per service**, enforced by the unique index `IX_Licenses_OneTrialPerCustomerService`
  (also after the trial is revoked or converted).
- **Extend trial** (⋮ menu): pick a later end date, up to `Billing__TrialMaxTotalMonths` from the start. An unpaid trial invoice moves
  to the new date; an Expired trial is switched back on. Editing the end date of a trial that already has its invoice is refused —
  use *Extend trial*. *Renew* on a trial converts it by hand (its unpaid trial invoice is voided).
  If the customer was already sent the trial reminder or trial invoice, extending emails them the new end date (once per date),
  with the still-valid pay link when there is an unpaid trial invoice.
- Grid: **Trial** badge, *Type* filter.

### Complimentary (free) licenses

- **Issue**: type *Complimentary*, or ⋮ → *Make complimentary* on an existing license. No price, never invoiced, never suspended for an
  overdue invoice, no expiry unless you set one. Making a license complimentary voids its unpaid invoices (pay links stop working),
  switches a Pending/Expired/overdue-suspended license on, keeps a manual suspension, and ends a trial. Invoices that already have a
  payment, or a Paystack payment in progress, are left open for you to refund or void (listed in the audit entry); a payment that
  still arrives does not change the license (audit: *Renewal skipped*).
- **Switch to paid** (⋮): set the price and the billing start date (default: current expiry if in the future, else today + 14 days).
  Free access runs until then; the first invoice is emailed now (or by the worker later if you untick it).
- Expiry reminders for a complimentary license with an end date use free-access wording (nothing to pay, no invoice or pay link).
- Grid: **Complimentary** badge, *Type* filter. All changes are in the audit log (*Made complimentary*, *Complimentary ended*,
  *Trial started/extended/converted*, *Trial reminder queued*).

**Migration** `AddTrialsAndComplimentaryLicenses`: adds `Licenses.IsTrial`, `TrialStartedAt`, `TrialReminderForEndsAt`, `IsComplimentary`,
`Invoices.IsTrialConversion` (all default false/null, existing licenses unchanged) and three partial indexes. Included in
`docs/migrations-idempotent.sql`.

## 11. License validation: signed tokens, devices and client integration

### Generate the signing key (once)

```bash
# Option A – the app itself (prints private PEM, public PEM and kid; nothing is stored)
dotnet Platform.Api.dll generate-license-key        # or: dotnet run --project API -- generate-license-key
# Option B – openssl
openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out license-signing.pem
openssl ec -in license-signing.pem -pubout -out license-signing.pub.pem
```

Put the **private** PEM in `Licensing__TokenSigningKey` (paste it with real newlines or as one line with `\n`).
Keep it secret; anyone with it can mint licenses. Products only ever need the public key / JWKS.

**Rotation:** generate a new key, move the old *public* PEM and its kid into
`Licensing__AdditionalPublicKeys__0__PublicKeyPem` / `__Kid`, set the new private key, restart.
Leave the old public key published for at least `OfflineGraceDays` (+ refresh), then remove it.

### Endpoints

`POST /api/licenses/validate` (header `X-Integration-Key`) — request (all new fields optional, old clients keep working):

```json
{ "licenseKey": "HOSTEL-XXXX", "serviceCode": "HOSTEL", "deviceId": "stable-machine-id", "deviceName": "Front desk PC" }
```

`200` — the license was judged:

```json
{ "isValid": true, "reason": "ok", "planName": "Growth", "expiresAt": "2027-01-31T00:00:00Z",
  "serverTime": "2026-11-02T10:00:00Z", "licenseId": "…", "token": "eyJhbGciOiJFUzI1NiIs…",
  "refreshAfter": "2026-11-02T22:00:00Z", "offlineGraceUntil": "2026-11-09T10:00:00Z",
  "maxDevices": 3, "activeDevices": 1 }
```

`isValid:false` (still 200) only when the license really is not usable; `reason` is one of
`invalid_key`, `invalid_integration_key`, `wrong_service`, `not_active`, `expired`, `suspended`, `revoked`,
`device_required`, `device_limit`. Bad input = `400 invalid_request`.
`wrong_service` means the `serviceCode` sent does not belong to the integration key's product (a product
misconfiguration); `invalid_integration_key` means the key itself is unknown or revoked. Leading/trailing whitespace
in the license key and the `X-Integration-Key` header is ignored, so pasted keys work.

`429 rate_limited` and `503 service_unavailable` mean **the server did not judge the license**. Both carry a
`Retry-After` header and `{ "reason", "message", "serverTime", "retryAfterSeconds", "retryable": true }` (no `isValid`).

`GET /api/licenses/keys` — JWKS (`{ "keys": [{ "kty":"EC","crv":"P-256","alg":"ES256","use":"sig","kid","x","y" }] }`), cacheable for an hour.

### Token format

Compact JWS, header `{"alg":"ES256","typ":"license+jwt","kid":…}`. Claims: `iss`, `sub`/`license_id`, `aud`/`product`,
`customer_id`, `plan`, `license_expires_at`, `device_id` (as sent), `trial`, `complimentary`, `iat`,
`server_time`, `refresh_after`, `grace_until`, `exp` (= `grace_until`).

### Client integration guide (what each product must do)

1. **Device id:** derive a stable id per installation (e.g. a GUID stored on first run, or a machine id) and send it
   with every validation. Changing it uses a new device slot.
2. **Validate on start-up and when `refreshAfter` passes.** Store the last `token` locally.
3. **Verify the token locally** with the JWKS (fetch and cache it; re-fetch when you meet an unknown `kid`):
   check the ES256 signature, `iss`, `aud`/`product` = your product code, `device_id` = your device id, and that
   now < `exp`. Use the token's `server_time` vs. your clock to spot a badly wrong clock.
4. **Offline / server trouble:** on network errors, `5xx`, `503` or `429`, keep running while the stored token is
   valid (until `grace_until`). Retry with exponential backoff + jitter (e.g. 30 s, 1 m, 2 m … max 30 m) and always honour
   `Retry-After`. Never treat these as "license invalid".
5. **Only lock the product on a `200` with `isValid:false`** (or once the stored token's grace has run out). Show the
   `message`; on `device_limit` tell the user to ask you to release an old device (Licenses → ⋮ → Devices).

### Devices

Each license has `MaxDevices` (set in Issue/Edit; 0 = unlimited). The first validation from a new `deviceId`
activates a slot (audited). Admins can see devices and release one in **Licenses → ⋮ → Devices**; the slot is
freed at once. Lowering the limit never removes active devices; it only blocks new ones.

### Proxies

Client-IP rate limiting uses the connection address. If the API sits behind a reverse proxy, set
`ForwardedHeaders__Enabled=true` and list the proxy in `ForwardedHeaders__KnownProxies__0`; the app logs a warning
once if traffic looks proxied while this is off. Leave it off on IIS in-process (MonsterASP).

### Database migration

This release adds the `LicenseDevices` table and a nullable `Licenses.MaxDevices` column
(migration `AddLicenseDevices`, included in `docs/migrations-idempotent.sql`). Run it before deploying.
