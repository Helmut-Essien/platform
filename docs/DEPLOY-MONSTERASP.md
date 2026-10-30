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

## 11. Public landing page and SEO

**Routing.** `GET /` (and `HEAD /`) serves the static, server-rendered `wwwroot/landing.html` (no WASM; critical CSS inline; one small deferred module, `wwwroot/js/landing.js`, adds the decorative WebGL hero, 3D tilt and scroll reveal after load, and the page is complete without it). Every other path is unchanged and still gets the admin SPA: `/login`, `/reset-password`, `/invoices/...`, `/pay/{token}`, and so on. Links already sent in emails, `Auth:ClientBaseUrl` and the Paystack `callback_url` keep working. The admin home moved from `/` to **`/dashboard`**. After login (or for an unsafe `returnUrl`) the app goes to `/dashboard`, and an in-app navigation to `/` redirects there. "Client login" is in the landing page footer only and links to `/login`, which goes to `/dashboard` after sign-in. The middleware is `PublicSiteExtensions.UsePublicSite()`, and it must stay before `UseBlazorFrameworkFiles/UseStaticFiles`.

**Fix included.** Pay-link tokens contain a dot (`{invoiceId}.{mac}`), so the default SPA fallback treated `/pay/{token}` as a file name and returned **404** on a direct load, e.g. from an email or the Paystack return. `MapPublicSiteFallbacks()` adds a dedicated `/pay/{**token}` fallback.

**Indexing.**
- Only `/` and its public assets are indexable: `robots.txt`, `sitemap.xml`, `og-image.png`, the favicons, the icons and `site.webmanifest`.
- Every other response (SPA, `/pay`, `/api`, `/health`, `_framework`) gets `X-Robots-Tag: noindex, nofollow`.
- `robots.txt` allows only `/$` and the public assets, and disallows everything else.

**Caching and compression.**
- `landing.html`: `public, max-age=300, must-revalidate` + ETag.
- `robots.txt`, `sitemap.xml`, `site.webmanifest`: 1 h.
- Icons and the social image: 1 day.
- The SPA `index.html`: `no-cache` (as before).
- The landing page, robots, sitemap, the SVG favicon and the manifest are Brotli/gzip compressed.

**Placeholders.** Contact details are filled in (Oct 2026). Only the optional product-names comment remains; run `grep -rn "TODO-HELMUT" Client/wwwroot/landing.html` and replace:
- the contact email (the `mailto:` links, the footer and the JSON-LD);
- the phone (the `tel:` link, the button text and the JSON-LD);
- the street, city and region (the JSON-LD and the footer);
- optionally, your product names (the comment in "What we do").

Then update `<lastmod>` in `sitemap.xml`. Keep the FAQ answers identical to the `FAQPage` JSON-LD; a test enforces this.

**Custom domain.** If the site moves off `helmutcode.runasp.net`:
- replace that host in `landing.html` (canonical, `og:*`, `twitter:*`, JSON-LD), `robots.txt` and `sitemap.xml`;
- set `Auth__ClientBaseUrl` and `Cors__AllowedOrigins` to the new origin.

**Manual check after deploy.**
1. `curl -sI https://helmutcode.runasp.net/` should return `200`, `text/html`, and no `X-Robots-Tag`.
2. `curl -sI https://helmutcode.runasp.net/dashboard` should include `X-Robots-Tag: noindex, nofollow`.
3. Open a real pay link from an invoice email; it should load the pay page, not a 404.
4. Validate with Google's Rich Results Test and the opengraph.xyz preview.
5. Add the property in Google Search Console and submit `/sitemap.xml`.
