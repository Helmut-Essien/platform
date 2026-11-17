# Product plans (built)

**Status: built** (branch `feat/product-plans`). This file describes what is in the code now. Migration `AddProductPlans` adds the `ProductPlans` table and `ServiceProducts.DefaultMaxDevices`; `docs/migrations-idempotent.sql` includes it.

Purpose: how Platform attaches a stable plan code to each product's license keys, and how that works with products such as NeedleBase.

This file is part of [platform-license-hub](SKILL.md).

## What was there before

`License.PlanName` is a required free-text string (max 100). The Issue License dialog used to default to `"Pro"`. Create did not trim; edit trimmed. The validate API returns `planName` on the JSON body and a `plan` claim on the signed token. Trial and complimentary flags are on the license and in the token (`trial`, `complimentary`), not on the JSON body. All of that is unchanged.

## As built

### Data

`ProductPlan` (`API/Entities/ProductPlan.cs`, table `ProductPlans`):

| Field | Meaning |
|-------|---------|
| `Id` | ULID string |
| `ServiceProductId` | FK to `ServiceProducts` (cascade: deleting a product, which is only allowed with no licenses, removes its plans) |
| `Code` | Stable, lowercase (`a-z`, `0-9`, single `-` or `_` between parts), max 50, never edited after creation |
| `DisplayName` | What the admin sees, max 100, editable |
| `SortOrder` | Dropdown order (ascending, then code) |
| `IsActive` | Inactive plans can't be chosen for new issues or edits |
| `DefaultAmount`, `DefaultTaxAmount`, `DefaultCurrency`, `DefaultPeriodMonths` | Optional default price (all null = none). Same rules as license billing (amount > 0, tax ≥ 0, 3-letter currency or the billing default when blank, period 1–36 months). UI prefill only |
| `CreatedAt`, `UpdatedAt` | Timestamps |

Unique index `IX_ProductPlans_ServiceProductId_Code`. `License.PlanName` **stores the code**; there is no `PlanId` column, so the validate body and token claim are untouched.

`ServiceProduct.DefaultMaxDevices` (nullable int): the device limit a new license of that product gets when the admin types none. `null` = server `Licensing:DefaultMaxDevices` (3), `0` = unlimited. Explicit values on the issue request still win. NeedleBase should be set to **1**. Applied server-side in `LicenseService.CreateAsync`, so it holds for API callers too; the Issue dialog shows it in the helper text. Create sends it (null = server default). On update, `defaultMaxDevices: null` means **unchanged** (an older client that omits it cannot wipe it); `clearDefaultMaxDevices: true` removes it, and the edit dialog sends that when the field is emptied.

### The check (and why deploying it is safe)

Implemented in `API/Services/ProductPlanRules.cs`, called from `LicenseService.CreateAsync` and `UpdateAsync` (trial and complimentary issues included):

- **A product with no plans keeps free text.** The name is trimmed and stored as typed (case kept). Deploying this change therefore blocks nothing: no product has plans until you add them.
- **The catalog is enforced per product, from the moment it has at least one plan.** The name is trimmed and lowercased and must equal the code of an **active** plan of *that* product, otherwise the request is rejected (400) with the list of active codes. A product whose plans are all inactive rejects every new issue ("has no active plans").
- **Edit exception:** if the edit names the license's *current* plan (ignoring case and surrounding spaces) the stored value is kept untouched even when it is not an active plan. So a key still on an unmapped old name (`Pro`) or on a deactivated plan can still have its expiry, price or devices edited. Changing to a different name still has to be an active plan. If the current name does match an active code, it is normalized to the code.
- Audit rows (`LicenseIssued`, `LicenseUpdated`) record the stored code.
- Invoices keep their own `PlanName` snapshot and are never rewritten.

### API (all admin-only, under `api/serviceproducts`)

| Route | What |
|-------|------|
| `GET {id}/plans` | The product's plans (`ServiceProductDto.Plans` also carries them in the list/get responses) |
| `POST {id}/plans` | Add a plan `{code, displayName, sortOrder, isActive}` (code trimmed + lowercased A-Z only; duplicate per product rejected, also under a race; display name one printable line, max 100; at most 100 plans per service) |
| `PUT {id}/plans/{planId}` | Edit display name, order, active flag (deactivate = `isActive:false`) |
| `DELETE {id}/plans/{planId}` | Only while no license of the product (revoked included) carries the code; otherwise deactivate |
| `GET {id}/plans/usage` | Distinct `PlanName` values on the product's licenses with counts: `licenseCount`, `remappableCount` (non-revoked), `isActivePlanCode` |
| `POST {id}/plans/remap` | `{fromPlanName, toCode, dryRun}`: move every **non-revoked** license whose plan name is exactly `fromPlanName` onto an active plan code of the same product |

Remap: exact, case-sensitive match on the old name (never guesses); revoked licenses are left alone and counted; suspended customers' licenses are included; one `SaveChanges` carries all license changes plus their audit rows (per-license `LicenseUpdated` with `source:"plan_remap"` and `previousPlanName`, plus one `LicensePlansRemapped` summary). The matching licenses are row-locked first (`SELECT … FOR UPDATE` in id order) so an edit in flight finishes first, or, if it starts later, gets the retryable 409 for its stale version; the remap itself never fails on a concurrent edit and never half-applies. Validation reads `PlanName` from PostgreSQL on every check, so there is no validation cache to invalidate. `usage` returns at most 500 names (busiest first). `dryRun:true` only reports counts. It can be repeated safely.

Plan changes are audited as `ProductPlanCreated`, `ProductPlanUpdated`, `ProductPlanDeleted`.

### Admin UI

- **Services page:** new "Plans" column (active count, "No active plans" when all are deactivated, or "Free text") and a Manage plans action opening `ProductPlansDialog`: plan table (code, name, order, active switch, edit, delete), an add/edit form, and a "Plan names on existing licenses" section listing each old name with its license count and a "Map to" picker (preview with a dry run, confirm, then map).
- **Add/Edit Service:** a "Default max devices" field.
- **Issue License:** after the service is chosen, Plan is a dropdown of that service's active plans (shows `DisplayName (code)`, stores the code). Changing the service clears the plan. A service without plans shows the old text field, with no `"Pro"` default and a hint to add plans. The max-devices helper shows the service default.
- **Edit License:** the same dropdown; a key on a plan that isn't selectable stays selectable as "(not in the plan list, current)" so it can be edited without being forced to move.
- The Licenses page re-reads the service list before opening either dialog so the plan list is never stale.

**Limits stay in each product, not in Platform.** Platform says which plan, whether the key is paid, trial or complimentary, and when it expires. Staff seats, feature flags and what the plan allows belong in the product (for NeedleBase, its `PlanCatalog`). No entitlements JSON.

**Upgrade or downgrade:** edit the **same key's** plan (and price or term if needed). Do not revoke and reissue: Platform allows only one open license per customer and product, and revoking would disconnect a shop that already attached that key.

**The validate contract does not change:** `POST /api/licenses/validate` and `GET /api/licenses/keys` stay as they are (a test freezes the response/request shape and checks the mapped code in the body and the token).

## Plan prices and scheduled price changes

**Default price.** `ProductPlanDto.defaultPrice` `{amount, taxAmount, currency, periodMonths}` or null. Create takes `defaultPrice`; update takes `defaultPrice` (null = unchanged, so old clients can't wipe it) and `clearDefaultPrice:true` to remove it. Issue License prefills the billing fields when a plan is picked (not for complimentary; trials behave as before); Edit License prefills when the plan is changed. The admin can override. The API never applies it on its own. The license keeps its own copy, so changing the plan price never moves existing licenses.

**Scheduled change** (`API/Services/PriceChanges/`):

| Route | What |
|-------|------|
| `POST api/serviceproducts/{id}/plans/{planId}/price-change/preview` | `{amount, taxAmount, currency, periodMonths?, effectiveFrom}` → every license on the plan code (revoked counted, not listed), old vs new price, `eligible` + `ineligibleReason` (complimentary, no price, already at this price, already scheduled at this exact price and date), existing pending date, `nextInvoiceAlreadyIssued`, `noticeDays`, `noticeSentOnConfirm`. Max 2,000 rows (`truncated`); licenses already scheduled at this exact price and date are listed last, so previewing again after scheduling a full page shows the rest |
| `POST api/serviceproducts/{id}/plans/{planId}/price-change` | Same plus `licenseIds` (the ticked ones, max 2,000) and `updatePlanDefaultPrice`. Returns scheduled / replaced / skipped / notices queued |
| `GET api/licenses/{id}/price-changes` | History (latest 50) |
| `POST api/licenses/{id}/price-changes/{changeId}/cancel` | Cancel a pending change (emails the customer only if the notice went out) |

- Table `LicensePriceChanges`: old and new price snapshot, `EffectiveFrom` (date), `PlanCode`, `BatchId`, `Status` Pending/Applied/Cancelled, `NoticeQueuedAt`, `AppliedInvoiceId`, cancel info, `Version` token. At most one Pending per license (partial unique index); scheduling a different change **replaces** it (old one cancelled first, same transaction); scheduling the identical change again is skipped, so a double submit sends no second email. Same suspended-customer query filter as License; service code uses `IgnoreQueryFilters`.
- **Apply:** `BillingService.CreateRenewalInvoiceAsync`, after the stale-invoice handling and under the license row lock, takes the pending change with `EffectiveFrom <= periodStart`, writes the new price (and period) onto the license, and bills the invoice at it. The license is re-read after its row lock, so an edit that committed while the invoice waited is billed as stored (or the run is skipped/409 if the expiry or eligibility moved). The change is marked Applied with `AppliedInvoiceId`. A change whose plan code no longer matches is cancelled instead (safety net). Issued invoices are never touched, so a license whose next invoice already exists gets the new price from the following period.
- **Notice:** `Billing:PriceChangeNoticeDays` (default 30, 0–365, 0 = off). `PriceChangeNoticeWorker` runs hourly and queues the email once `EffectiveFrom - days` has arrived (concurrency token, so parallel runs send once). Scheduling inside the window queues it immediately (not for suspended customers, who are told once reinstated). The email says when the next invoice is already issued at the old price. A batch that loses a race to another run retries its other rows straight away. Display names are used, all values HTML-encoded.
- **Manual edits (rule):** anything that changes the license price tuple (amount, tax, currency, period) or plan cancels the pending change: Edit License, Renew with a new price, plan remap, Make complimentary. Revoke cancels without an email. If the customer was notified, a "price change cancelled" email goes out (except revoke). Edits that leave the price alone (expiry, devices, auto-renew) keep it. Edit License shows the pending change and warns when saving would cancel it.
- **Locks:** plan rows (shared; exclusive when the plan default is also updated) → license rows (`FOR UPDATE` in id order) → change rows. Billing: renewal advisory lock → invoice rows → license row → change rows. Preview and the list are batch queries (no N+1).
- Audit: `PlanPriceChangeScheduled` (summary), `LicensePriceChangeScheduled`, `LicensePriceChangeApplied`, `LicensePriceChangeCancelled`, `LicensePriceChangeNoticeQueued`; the renewal audit carries `priceChangeId`.

## How it works with NeedleBase

NeedleBase's product code in Platform is `NEEDLEBASE` (the token audience). Its plan codes must be:

| Code | Display name | Sort order |
|------|----------------|-----------|
| `starter` | Starter | 10 |
| `workshop` | Workshop | 20 |
| `production` | Production | 30 |

NeedleBase reads the code from the signed token, stores it as `Business.LicensePlan`, and applies its own limits. That product's rules (staff caps that exclude the Owner, complimentary keys are unlimited, trial keys act as Starter until expiry while carrying the plan they will convert to, unknown plans fail closed to Starter, Free never calls Platform) live in the NeedleBase repo at `/home/helmut/Documents/My Projects/NeedleBase/.cursor/skills/needlebase-plans/SKILL.md`. Do not duplicate those limits here.

Set the NeedleBase service's **Default max devices to 1** (one shop per key); a second shop with the same key must get `device_limit`.

For a trial key, pick the plan the shop will convert to (`workshop` or `production` if that is the intent). NeedleBase treats the trial as Starter until expiry, then applies the carried plan.

## Rollout runbook (after deploy)

1. Deploy. The migration only adds a nullable column and a new empty table, so nothing changes for existing keys or products. Every product is still on free text.
2. In **Services → ⋮ Manage plans** for a product, open the "Plan names on existing licenses" list (or `GET .../plans/usage`) to see every distinct name per product, especially `NEEDLEBASE`.
3. Add the product's real plans (NeedleBase: `starter`, `workshop`, `production`). **From the first plan onward that product enforces the catalog on create/edit**, so do steps 3 and 4 together. Existing keys keep validating and can still be edited while unmapped.
4. Map each old name to a plan (preview, confirm). Do not auto-guess names from other products onto NeedleBase.
5. Set NeedleBase's Default max devices to 1.
6. Invoices stay as they are. Attached NeedleBase shops pick up the new code on their next successful re-check (validation reads the database, so the change is live at once; tokens already issued keep the old plan until they refresh).

## Display names (what people read)

`License.PlanName` stays the **code** (stored value, validate answer `planName`, token `plan` claim: the contract). Wherever a human reads the plan, Platform shows the plan's **display name** (`ProductPlanDisplay`):

- `LicenseDto.planDisplayName` (always filled; equals `planName` for unmapped legacy names, deleted plans and products without plans). The Licenses grid, Rotate/Activate dialogs and the edit dialog's "current plan" entry use it; the grid search also matches the display name. List pages use **one** query for all plans of the products on the page; single licenses one indexed lookup.
- Match is exact on the lowercase code. Inactive plans still show their display name.
- **Invoices snapshot the display name** at creation (`Invoice.PlanName`, "License Workshop" descriptions, renewal descriptions, trial conversion). That makes invoice emails, PDFs and receipts read well with no template logic, and a later rename of the plan never rewrites an issued invoice. A plan *code* typed into the manual invoice form is translated the same way; other text is kept as typed. Invoices created before this change are not rewritten.
- The license key email shows the display name in its Plan row.
- The Validate page in the admin UI is a raw view of the API answer, so it keeps showing the code.
- The Licenses grid sorts by the stored code.

## Risks and notes

- Adding a product's first plan switches enforcement on for that product immediately; keys not yet mapped keep working and stay editable, but can't be moved to another unmapped name.
- A product whose plans are all deactivated can't issue new licenses.
- Remap touches licenses in one transaction under row locks; an edit that loses the race gets a 409 "refresh and try again" and nothing is half-applied.
- Deleting a plan and issuing **or editing** a license onto it are serialized by a row lock on the product's plans (license create and update run in a transaction and take the shared lock first; plan delete takes the exclusive lock and then checks for licenses on the plan). Lock order is always plan rows, then license rows; remap only locks license rows, so create/edit/delete/remap cannot deadlock. A key therefore can't end up on a deleted plan.
- API callers that PUT a service without `defaultMaxDevices` leave it unchanged (use `clearDefaultMaxDevices` to clear).
- Plan codes are lowercased A-Z only (no culture folding of look-alike letters).
- Display names must be one printable line (no control characters, line/paragraph separators or bidi overrides), max 100; a service holds at most 100 plans. Both are enforced server-side on create (and the name rule on edit).
- A license edit sends its Redis calls (validation-cache invalidate, deny on expiry) only after the commit, so slow Redis never holds the plan or license locks.
- Tokens issued before a remap keep the old `plan` until the product re-validates.
- A price change scheduled with an effective date after a license's next invoice period start waits for the following renewal; preview shows the old vs new price, not when each license will switch.

## Related

| Need | Where |
|------|-------|
| License hub (entities, validate, phases) | [SKILL.md](SKILL.md) |
| Admin UI | [platform-admin-ui](../platform-admin-ui/SKILL.md) |
| NeedleBase plan limits (other repo) | `/home/helmut/Documents/My Projects/NeedleBase/.cursor/skills/needlebase-plans/SKILL.md` |
