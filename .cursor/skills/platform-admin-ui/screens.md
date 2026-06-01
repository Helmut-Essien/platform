# Screen specifications

All screens use [design-system.md](design-system.md) tokens. Responsive behavior per [wireframes-phase1.md](wireframes-phase1.md). Layout: `MainLayout` shell or `LoginLayout` for `/login`.

**As-built Client code:** [implementation-patterns.md](implementation-patterns.md).

Feedback states (loading, empty, error, snackbar): [design-system.md](design-system.md#feedback-states).  
Destructive confirmations: [design-system.md](design-system.md#destructive-confirmation-dialog).

---

## Global page layout (all authenticated pages)

| Element | Implementation |
|---------|----------------|
| Root wrapper | `<div class="{page}-page page-content">` |
| Title | `<h1 class="page-title">` — color `--primary` (`#92d959`) |
| Subtitle | `<p class="page-subtitle">` |
| Width | `.page-content` centered; 96rem → 100rem → 112rem at md/lg/xl breakpoints |
| Drawer title | `.page-title-drawer` on customer detail |
| Invoice detail | `<header class="invoice-detail-header">` + `.page-title` (no `PageHeader` component) |

Boot splash (before WASM loads): logo + green progress bar — [implementation-patterns.md](implementation-patterns.md#wasm-boot-splash).

Dialogs: provider `MaxWidth.Medium`; confirms use `PlatformDialogOptions.Confirm`.

---

## Form patterns

Every form in the application follows these rules. Deviate only with explicit reason.

### MudForm + validation

- Wrap all fields in `<MudForm @ref="_form" Model="_model" @bind-IsValid="_formValid">`.
- Every input binds via `@bind-Value="_model.Field"` with `For="@(() => _model.Field)"` for MudBlazor's validation display.
- Required fields: set `Required="true"` on the component AND `[Required]` data annotation on the model property.
- Email fields: `InputType="InputType.Email"` for browser-native email validation.
- Numeric fields: `InputType="InputType.Number"` or `[Range]` attribute on the model.
- Submit button: `Disabled="@(!_formValid || _busy)"` — disabled until all fields valid.
- API call only proceeds after `await _form.Validate(); if (!_formValid) return;`.
- Validation error messages render **inline below each field** (MudBlazor default with `For`). Error text color: `--accent`.

### Error display

- API errors (400, 401, 409, etc.): show `MudAlert` Severity.Error at the top of the form with the server's `message`. Dismissible.
- Inline field errors: handled automatically by MudBlazor via `For` and `[Required]` / `[EmailAddress]` / `[Range]` annotations.
- Transient success/failure: `ISnackbar` — see [Snackbar](#snackbar-canonical) below.

### Snackbar (canonical)

**Do not use MudBlazor’s default green success.** Configure theme/CSS so:

| Severity | Background | Text |
|----------|------------|------|
| Success | `--accent` | `--bg-base` |
| Error | dark surface / Severity.Error | `--text-primary` |

```csharp
Snackbar.Add("Customer saved.", Severity.Success);
Snackbar.Add(apiMessage, Severity.Error);
```

### Destructive confirmation

Before revoke, suspend, or delete — use dialog per [design-system.md](design-system.md#destructive-confirmation-dialog):

- Title: verb + entity
- Cancel (text) = **default focus**
- Confirm (filled `--accent`) = destructive verb label
- Escape cancels; Enter does not confirm unless Confirm is focused

### Keyboard + focus

- Enter key submits the primary form action (`OnKeyUp` handler checks `e.Key == "Enter"`).
- Tab order follows visual field order.
- Focus ring: per [design-system.md](design-system.md#focus-rings) — +2px offset default; nav items −2px inset
- Escape closes dialogs (`MudDialog` handles this by default).
- Honor `prefers-reduced-motion` for animations (design-system).

### Loading state

- On submit: set `_busy = true`, show `MudProgressCircular` Size.Small inline on the button + "Processing..." text.
- Inputs set `Disabled="_busy"` during submission.
- On completion (success or error): set `_busy = false`.

### Password fields

Every password field uses this pattern:

```razor
<MudTextField @bind-Value="_model.Password"
              For="@(() => _model.Password)"
              Label="Password"
              Variant="Variant.Outlined"
              InputType="@(_passwordVisible ? InputType.Text : InputType.Password)"
              Adornment="Adornment.End"
              AdornmentIcon="@(_passwordVisible ? Icons.Material.Filled.VisibilityOff : Icons.Material.Filled.Visibility)"
              OnAdornmentClick="TogglePasswordVisibility"
              FullWidth="true"
              Disabled="_busy"
              autocomplete="current-password"
              OnKeyUp="OnKeyUp" />
```

```csharp
private bool _passwordVisible;
private void TogglePasswordVisibility() => _passwordVisible = !_passwordVisible;
```

### Model annotations (example)

```csharp
private class LoginModel
{
    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Enter a valid email")]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Password is required")]
    [MinLength(8, ErrorMessage = "Password must be at least 8 characters")]
    public string Password { get; set; } = "";
}
```

---

## Login `/login`

Follows [Form patterns](#form-patterns).

**Layout:** `LoginLayout` — no shell, no drawer.

### Component spec

| Element | Component | Spec |
|---------|-----------|------|
| Background | — | `--bg-base` + radial gradient glow at edges |
| Card | `MudPaper` Elevation="0" | max-width 30rem, `--bg-surface` at 85% opacity, `1px solid` `--accent` at 10% opacity, glass morphism (`backdrop-filter: blur(24px)`) |
| Card corners | | `border-radius: 1.5rem` xs → `1.75rem` sm → `2rem` md+ |
| Card padding | | `pa-5` xs → `pa-sm-7` sm → `pa-md-8` md+ |
| Logo | `<img src="images/login-logo.png">` | HelmutCode logo, `object-fit: contain`, green glow shadow |
| Subtitle | `MudText` Typo.body2 | "Sign in to manage licenses and customers", `--text-secondary`, centered |
| Divider | `MudDivider` | subtle, `--accent` at 8% opacity |
| Email | `MudTextField` | Outlined, full width, label "Email", type email, autocomplete "email" |
| Password | `MudTextField` | Outlined, full width, label "Password", toggle visibility adornment, autocomplete "current-password" |
| Submit | `MudButton` Variant.Filled | **`--accent`** bg, `--bg-base` text (accent CTA — see design-system CTA hierarchy), full width, min-height 3rem |
| Submit hover | | glow shadow (`0 4px 20px` `--accent` at 25%), translate up 1px |
| Submit loading | `MudProgressCircular` | Size.Small inline spinner + "Signing in..." text |
| Error | `MudAlert` Severity.Error | Outlined, border-radius 0.75rem, closable |
| Success | | Redirect to `/`, JWT stored via `TokenStorage` |
| Animation | CSS keyframe | Slide up + fade in 0.5s, disabled on `prefers-reduced-motion` |
| Keyboard | | Enter submits form. Tab order: Email → Password → Sign in |
| Focus | | `2px solid --accent` offset 2px |

### Logo responsive sizing

| Viewport | Logo size | Logo border-radius |
|----------|-----------|-------------------|
| xs (<360px) | 4.5rem | 1.125rem |
| xs (360–599px) | 5.5rem | 1.25rem |
| sm (600–959px) | 7rem | 1.75rem |
| md+ (≥960px) | 8.5rem | 1.75rem |
| Short viewport (<500px h) | 3rem | 0.75rem (subtitle hidden) |

### API

`POST /api/auth/login` → `{ token, email, expiresAt }`. AllowAnonymous.

---

## Dashboard `/`

See [wireframes-phase1.md](wireframes-phase1.md) for full layout (reference mock May 2026).

### Page header

| Element | Spec |
|---------|------|
| Title | H1 “Dashboard”, `--primary`, 32px / weight 600 |
| Subtitle | “Platform overview and performance metrics.”, `--text-secondary`, 14px |

### KPIs

`GET /api/dashboard/stats` → `{ CustomerCount, ActiveLicenses, ExpiringWithin30Days, UnpaidInvoices }`

4-column grid (`1 → 2 → 4` cols). Each card:

| Part | Spec |
|------|------|
| Label | JetBrains Mono uppercase, `--text-secondary` |
| Icon | Material icon top-right (group, vpn_key, schedule, receipt_long) |
| Value | 32px bold `--accent` |
| Footnote | JetBrains Mono 11px — contextual (e.g. “Healthy ecosystem”, “Attention required”) |
| Interaction | Whole card clickable; hover border `--primary` |

| KPI card | Click-through |
|----------|---------------|
| Total Customers | → `/customers` |
| Active Licenses | → `/licenses` |
| Expiring Within 30 Days | → `/licenses` |
| Unpaid Invoices | → `/invoices` |

### Main layout (lg+)

Asymmetric **2:1** grid:

| Column | Content |
|--------|---------|
| Left (2/3) | Recent Activity timeline |
| Right (1/3) | Quick Actions stack + Platform Health |

### Recent activity timeline

`GET /api/audit-logs?limit=10`

Custom timeline (not MudTimeline):

| Element | Spec |
|---------|------|
| Header | “Recent Activity” + “Live Stream” badge |
| Events | Vertical line `--border-subtle`; dot per event |
| Title | `{Action} — {CustomerName}` with customer in `--primary` |
| Summary | Details JSON excerpt or “Recorded by {admin}” |
| Timestamp | Relative uppercase mono (“2 minutes ago”) |
| Dots | Latest = `--primary`; destructive actions = `--text-error`; else neutral |
| Empty | Inline message when no events |
| Footer | Full-width “View Full Audit Log” → `/audit` |

Resolve customer names via customer list lookup on `CustomerId`.

### Quick actions (sidebar)

Stacked vertical buttons:

| Button | Style | Target |
|--------|-------|--------|
| + New Customer | Primary fill `--primary` | `/customers?add=true` |
| Issue License | Outlined | `/licenses?add=true` |
| Generate Integration Key | Outlined | `/integration-keys` |

### Platform Health (sidebar)

Decorative module — static bars until monitoring API exists:

- API Uptime 99.98%
- Sync Queue 0%

Watermark analytics icon at 10% opacity.

### Loading

Skeleton: 4 KPI rectangles + main panel block.

### Components

Custom CSS in `Dashboard.razor.css`; `MudIcon`, `MudSkeleton` for loading only.

---

## Customers `/customers`

**Purpose:** Manage organizations. Entry point to customer licenses, invoices, and audit history.

Reference mock: Customers page (May 2026) — filter bar, styled grid, right detail drawer.

### Page header

| Element | Spec |
|---------|------|
| Title | `<h1 class="page-title">` “Customers” |
| Subtitle | `.page-subtitle` — “Manage platform accounts…” |
| CTA | Primary button “New Customer” with add icon → `CustomerCreateDialog` |

### Filters bar

`.customers-filters` — search + two `MudSelect` controls (not a separate panel card in current build).

| Control | Spec |
|---------|------|
| Search | `MudTextField`, debounced 300ms — name, email, ID |
| Status | `MudSelect` string: **`"all"`** (default) / `"active"` / `"suspended"` — labels All Statuses, Active, Suspended. **Do not use `""` or Clearable.** |
| Created | `MudSelect` string: **`"all"`** (default) / `"30d"` — All Time, Last 30 Days (client-side) |

### Grid

`GET /api/customers?page=1&pageSize=25` — `MudDataGrid` inside styled panel.

| Column | Spec |
|--------|------|
| Name | Avatar initials (2 letters) + bold name + mono short ID subtitle; row hover name → `--primary` |
| Contact Email | `--text-secondary` |
| Contact Phone | `--text-secondary` or em dash |
| Status | Badge — **Active**: primary/10 bg; **Suspended**: elevated gray |
| Licenses | Centered JetBrains Mono |
| Created At | `MMM dd, yyyy` |
| Actions | `MudMenu` MoreVert — View, Edit, Licenses, Suspend/Reactivate |

Row click opens detail drawer. Pagination via `MudDataGridPager`.

### Row actions

| Action | UX |
|--------|-----|
| Edit | `CustomerEditDialog` → `PUT /api/customers/{id}` |
| View Licenses | Navigate `/licenses?customerId={id}` |
| Suspend | Confirm dialog → `POST /api/customers/{id}/suspend` |
| Reactivate | Confirm dialog → `POST /api/customers/{id}/reactivate` |

### Create customer

`CustomerCreateDialog` (MudDialog) — Name, ContactEmail, ContactPhone, InternalNotes. `POST /api/customers` → 201.  
Also opened via `/customers?add=true`.

### Detail drawer (`CustomerDetailDrawer`, 480px right / full-width xs)

| Section | Spec |
|---------|------|
| Header | Avatar, name, status badge, close button |
| Tabs | Custom mono tabs: Profile · Licenses · Invoices · Audit |
| Profile | Info cards grid (email, phone, ID, created, license count) + notes blockquote |
| Licenses / Invoices / Audit | Compact tables or empty state with icon |
| Footer | “Edit Record” (outlined) + “Suspend” (error) or “Reactivate” (primary) |

Overlay backdrop on open. Tabs lazy-load tab data on first select.

### Empty state

Centered message + “New Customer” primary CTA.

### Components

`Customers.razor.css`, `CustomerDetailDrawer.razor.css`, `CustomerCreateDialog`, `MudDataGrid`, `MudMenu`, `MudDrawer`

### API

`GET/POST /api/customers`, `PUT /api/customers/{id}`, `POST /api/customers/{id}/suspend`, `POST /api/customers/{id}/reactivate`

---

## Service Catalog `/services`

**Purpose:** CRUD `ServiceProduct`. Entry point to integration keys.

Reference mock: Service Catalog page (May 2026) — success banner, stat bento, styled table, insights row.

### Page header

| Element | Spec |
|---------|------|
| Title | H1 “Service Catalog”, 32px `--text-primary` |
| Subtitle | “Manage microservices, API endpoints, and system-level integrations.” |
| CTA | Primary button “Add Service” with add icon → `ServiceProductCreateDialog` |

### Post-create success banner

After `POST /api/serviceproducts` → 201, show inline banner (not modal):

| Element | Spec |
|---------|------|
| Container | `--surface-container`, border `primary/20`, flex row on md+ |
| Icon | Filled check circle, `--primary` |
| Title | “Service "{Name}" created successfully.” |
| Body | “Ready for deployment. You need a secure key for API access.” |
| CTA | Primary “Generate Integration Key” with key icon → `/integration-keys?productId={id}` |

Dismisses when user clicks Generate (navigates to Integration Keys). No separate Skip — admin can ignore banner.

### Stats bento (when catalog non-empty)

4-column grid (1 → 4 cols responsive), `--surface-container` cards:

| Stat | Source |
|------|--------|
| Active Services | Count where `IsAvailableForSale` |
| System Uptime | Decorative static 99.98% |
| Total Licenses | Sum of `LicenseCount` across products |
| Key Coverage | % products with `HasActiveIntegrationKey` + progress bar |

### Grid

`GET /api/serviceproducts` — `MudTable` inside styled panel (`Services.razor.css`).

| Column | Spec |
|--------|------|
| Name | Status dot (primary = available, gray = unavailable) + bold name |
| Code | Mono badge on `--bg-elevated`, `--primary` text |
| Description | `--text-secondary` or em dash |
| Available | `MudSwitch` inline — calls `PUT /api/serviceproducts/{id}` on toggle |
| Int. Key | **Active**: mono chip `{CODE}_KEY`, clickable → `/integration-keys?productId={id}`. **None**: uppercase error-toned badge |
| Actions | Edit + View Keys icon buttons; visible on row hover |

Table footer: “Showing 1–N of N results” mono caption on `--surface-container-low`.

### Insights row (when catalog non-empty)

2-column grid (stacks on mobile):

| Panel | Spec |
|-------|------|
| Service Performance Matrix | Decorative bar chart placeholder; caption “License volume distribution across catalog services.” |
| Automated Health Checks | Glass card with sparkle icon; catalog health copy |

### Add service dialog

`ServiceProductCreateDialog` — follows [Form patterns](#form-patterns).

| Field | Component | Notes |
|-------|-----------|-------|
| Warning | `MudAlert` Severity.Warning | Keys cannot be recovered after generation |
| Name | `MudTextField` | Required, max 200 |
| Code | `MudTextField` | Required, max 50, auto-uppercase |
| Description | `MudTextField` multiline | Optional, max 2000 |
| Available for sale | `MudSwitch` | Default true |

### Edit service dialog

`ServiceProductEditDialog` — code readonly display; Name, Description, Available for sale.

### Empty state

Centered message + “Add Service” primary CTA inside table panel.

### Loading

Centered `MudProgressCircular` — no skeleton wrapper that hides the grid (avoid ServerData chicken-and-egg).

### Responsive

| xs (<600px) | md+ (≥768px) |
|-------------|--------------|
| Stats 1-col | Stats 4-col |
| Header stacks | Header row, CTA right |
| Insights stack | Insights 2-col |
| Horizontal scroll table | Full table |

### Components

`Services.razor.css`, `ServiceProductCreateDialog`, `ServiceProductEditDialog`, `MudTable`, `MudSwitch`, `MudIconButton`, `MudDialog`, `MudAlert`

### API

`GET/POST /api/serviceproducts`, `PUT /api/serviceproducts/{id}`, `POST /api/integration-keys?serviceProductId={id}`

---

## Licenses `/licenses`

**Purpose:** Global license lifecycle — issue, activate, renew, suspend, revoke.

Reference mock: Licenses page (May 2026) — filter grid, multi-select table, glass bulk bar.

### Page header

| Element | Spec |
|---------|------|
| Title | H1 “Licenses”, 32px `--text-primary` |
| Subtitle | “Manage software entitlements and API access keys.” |
| CTA | Primary button “Issue License” → `LicenseIssueDialog` |

### Filters grid

4-column grid (1 → 4 cols), uppercase mono labels:

| Filter | Notes |
|--------|-------|
| Customer | All + list; server `customerId` filter |
| Service | All + products; client filter |
| Status | All + enum; client filter |
| More Filters | Toggles search panel |

Search (More Filters): debounced — customer, service code, plan.

### Grid

`GET /api/licenses?page=1&pageSize=25&customerId=` — `MudDataGrid` multi-select in `.licenses-table-panel`.

| Column | Spec |
|--------|------|
| ☐ | Select column |
| Customer | Name + mono `ID: {shortId}-{NAME}` |
| Service | Product name (fallback code) |
| Plan | Plan name |
| Status | Pill — Active primary fill; Expired error; Pending/Suspended outline; Revoked muted |
| Expires | Mono date; error when Expired |
| Key Sent | YES/NO chips with icons |
| Actions | `MoreVert` menu (status-dependent) |

### Bulk actions bar

Fixed bottom glass panel when rows selected (see [design-system bulk actions bar](design-system.md#component-tokens)): **Resend Keys** (info — API TBD), **Renew** (single selection), **Revoke** (bulk confirm per [destructive dialog](design-system.md#destructive-confirmation-dialog)), close to clear.

### Issue License dialog

`LicenseIssueDialog` — Customer, Service, Plan, Expires. `POST /api/licenses` → Pending.

### Activate License flow (two-step)

**Step 1 — Billing modal.** Follows [Form patterns](#form-patterns). Only available on Pending / Suspended / Expired licenses.

| Field | Component | Notes |
|-------|-----------|-------|
| Subtotal | `MudTextField` numeric | Required, decimal |
| Tax Amount | `MudTextField` numeric | Required, decimal |
| Currency | `MudTextField` | Default "USD", max 3 chars |
| Due Date | `MudDatePicker` | Optional |
| Description | `MudTextField` | Optional |

Info text: "An invoice will be created and the license key emailed to the customer."

`POST /api/licenses/{id}/activate` — API generates license key, creates invoice (status Sent), emails customer, audit logs `LicenseActivated` + `InvoiceSent` + `InvoiceLinkedToLicense`.

**Step 2 — One-time key reveal.** Immediately after activation succeeds:

- Dialog title: "License Activated" with success icon
- `<code class="license-key">` displays plain key (JetBrains Mono, `--accent` text, `--bg-elevated` background, 0.25rem+ padding, border-radius 4px, `1px solid --border-subtle`)
- `MudIconButton` copy → Snackbar "Copied"
- "This key has been emailed to {customer email}"
- Warning: "Copy this key now. It cannot be retrieved again after you close this dialog."
- `[I've Saved It]` button dismisses. No dismiss-by-click-outside. No close X.

### Renew License flow

Follows [Form patterns](#form-patterns). Same two-step modal as Activate, plus optional new `ExpiresAt` picker.

`POST /api/licenses/{id}/renew` — generates new key, extends expiry, creates new invoice, audit logs `LicenseRenewed` + `LicenseKeyRotated`.

### Suspend / Revoke / Update

| Action | UX | API |
|--------|----|-----|
| Suspend | Confirm dialog. "License will be denied immediately." | `POST /api/licenses/{id}/suspend` |
| Revoke | Destructive confirm. "Permanent — cannot be undone." | `POST /api/licenses/{id}/revoke` |
| Update | Inline or dialog: Plan Name (max 100), ExpiresAt. Follows [Form patterns](#form-patterns). Cannot update Revoked. | `PUT /api/licenses/{id}` |

### Customer-scoped view

Navigate `/licenses?customerId={id}`. Customer filter pre-selected. `?add=1` opens Issue dialog.

### Responsive

| xs (<600px) | md+ (≥768px) |
|-------------|--------------|
| Filters 1-col | Filters 4-col |
| Header stacks | Header row |
| Bulk bar wraps | Bulk bar centered |

### Components

`Licenses.razor.css`, `LicenseIssueDialog`, `ActivateLicenseDialog`, `RenewLicenseDialog`, `LicenseEditDialog`, `LicenseActionResultDialog`, `MudDataGrid`, `MudMenu`

### API

`GET/POST /api/licenses`, `POST /activate`, `POST /renew`, `POST /suspend`, `POST /revoke`, `PUT /api/licenses/{id}`

---

## Invoices `/invoices`

**Purpose:** List and manage bills. Record payments.

Reference mock: Invoices page (May 2026) — stats bento, filter panel, styled grid, insights row.

### Page header

| Element | Spec |
|---------|------|
| Title | H1 “Invoices”, 32px `--primary` |
| Subtitle | “Manage billing, customer accounts, and transaction history.” |
| CTAs | Primary “CREATE INVOICE” + outlined “EXPORT CSV” |

### Stats bento (when invoices exist)

4-column grid:

| Stat | Source |
|------|--------|
| Total Revenue | Sum `TotalAmount` for Paid invoices |
| Outstanding | Sum `AmountDue` for Sent/PartiallyPaid/Overdue |
| Paid This Month | Paid invoices with `IssueDate` in current month |
| Quick Filter | Chip toggles: ALL · UNPAID · SENT |

Footnotes use real overdue/transaction counts; revenue trend line decorative (+12.5%).

### Filters panel

`--surface-container-low` container, 4-col grid:

| Filter | Component |
|--------|-----------|
| Customer | Search text field |
| Status | Select (All + enum) |
| Date Range | `MudDateRangePicker` on IssueDate |
| Actions | RESET + FILTER buttons |

### Grid

`GET /api/invoices?page=1&pageSize=25&customerId=` — `MudDataGrid` in `.invoices-table-panel`.

| Column | Spec |
|--------|------|
| Invoice # | Mono primary link text; row click → detail |
| Customer | Circle avatar initials + name |
| Total / Paid / Due | Right-aligned mono amounts; due bold error when Overdue |
| Status | Pill badge — Paid primary-container; Sent outline; Partially Paid outline primary; Overdue error fill |
| Issue Date | `MMM dd, yyyy` |
| Actions | `MoreVert` — View, Record payment, Void (when allowed) |

Overdue rows: subtle error-container background tint.

### Insights row

2:1 grid (stacks mobile):

| Panel | Spec |
|-------|------|
| Automated Billing Rules | Decorative card + VIEW SETTINGS info snackbar |
| Q3 Projection | Revenue projection bar from paid/revenue ratio |

### Create Invoice

`CreateInvoiceDialog` via CREATE INVOICE. `POST /api/invoices` → navigate to detail.

### Export CSV

Client-side export of current filtered grid rows via `platformDownloadText`.

### Detail `/invoices/{id}`

Unchanged — see existing detail spec below.

### Empty state

Centered message + CREATE INVOICE CTA.

### Responsive

| xs | md+ |
|----|-----|
| Stats/filters stack | 4-col stats + filters |
| Insights stack | 2:1 insights |

### Components

`Invoices.razor.css`, `CreateInvoiceDialog`, `MudDataGrid`, `MudMenu`, `MudDateRangePicker`

### API

`GET/POST /api/invoices`, `POST /api/invoices/{id}/void`, `POST /api/invoices/{id}/receipts`

### Status chips (detail reference)

| Status | Variant |
|--------|---------|
| Draft | Text |
| Sent | Outlined |
| PartiallyPaid | Outlined |
| Paid | **Filled** |
| Overdue | **Filled** |
| Void | Text |

### Filters (legacy detail)

Customer autocomplete also supported via customer ID query param on API.

### Create Invoice

Follows [Form patterns](#form-patterns).

`[+ New Invoice]` → `MudDialog`. `POST /api/invoices`.

| Field | Component | Notes |
|-------|-----------|-------|
| Customer | `MudAutocomplete` | Required, async search |
| Service | `MudSelect` | Optional |
| License | `MudSelect` | Optional |
| Status | `MudSelect` | Default Sent |
| Subtotal | `MudTextField` numeric | Required |
| Tax Amount | `MudTextField` numeric | Required |
| Currency | `MudTextField` | Default "USD" |
| Due Date | `MudDatePicker` | Optional |
| Plan Name | `MudTextField` | Optional |
| Description | `MudTextField` | Optional |
| Internal Notes | `MudTextField` multiline | Optional |

Invoice number auto-generated. Audit logs `InvoiceSent` (or `InvoiceCreated` if Draft).

### Detail `/invoices/{id}`

`GET /api/invoices/{id}` → full invoice with customer, service, license links, and receipts list.

**Header block:** Invoice #, Status chip, Issue Date, Due Date. Links to Customer, Service, License (if linked).

**Amount block:** Subtotal, Tax, Total, Paid (AmountPaid), Due (AmountDue).

**Receipts table:** Receipt #, Amount Paid, Payment Method, Paid At, Reference, Notes.

**Actions:**

| Action | UX | Condition |
|--------|----|-----------|
| Record Payment | `MudDialog` (see below) | Invoice not Void or Paid |
| Void Invoice | Confirm dialog | Status is Draft / Sent / Overdue AND zero receipts |
| Back | `MudLink` | → `/invoices` |

### Record Payment dialog

Follows [Form patterns](#form-patterns).

`POST /api/invoices/{invoiceId}/receipts` → 201. Receipt number auto-generated `RCP-{year}-{00000..}`.

| Field | Component | Notes |
|-------|-----------|-------|
| Amount | `MudTextField` numeric | Required, > 0, ≤ AmountDue |
| Payment Method | `MudSelect` | BankTransfer · Card · Cash · MobileMoney · Other |
| Reference | `MudTextField` | Optional, max 200 |
| Paid At | `MudDatePicker` | Optional |
| Notes | `MudTextField` | Optional, max 2000 |

After recording, invoice status auto-recalculates:
- `AmountPaid >= Total` → Paid
- `AmountPaid > 0` → PartiallyPaid
- `AmountPaid == 0 AND DueDate < now AND status == Sent` → Overdue

Audit logs `ReceiptRecorded`.

### Responsive

| xs (<600px) | sm (600–959px) | md+ (≥960px) |
|-------------|----------------|--------------|
| Card list | Card list | `MudDataGrid` |
| Detail: single column | Detail: 2-col amounts | Detail: 2-col layout |
| Record payment: bottom sheet | Centered 400px | Centered 480px |
| Receipts: compact list | Receipts: compact table | Receipts: full table |

### Components

`MudDataGrid`, `MudChip`, `MudAutocomplete`, `MudSelect`, `MudTextField`, `MudDatePicker`, `MudDialog`, `MudLink`, `MudAlert`, `MudSnackbar`

### API

`GET/POST /api/invoices`, `GET /api/invoices/{id}`, `POST /api/invoices/{id}/void`, `POST /api/invoices/{id}/receipts`

---

## Integration Keys `/integration-keys`

**Purpose:** Manage API keys that authorize external license validation. One active key per service product.

Reference mock: Integration Keys page (May 2026) — security alert, stats row, bento key cards.

### Page header

| Element | Spec |
|---------|------|
| Title | H1 “Integration Keys”, 32px `--primary` |
| Subtitle | “Manage API credentials and authentication tokens for your enterprise services.” |
| CTA | Primary “Generate New Key” → `IntegrationKeyGenerateDialog` |

### Security notice

Error-toned alert banner (not MudAlert inline): warning icon + bold “Security Notice” — keys cannot be recovered after creation.

### Stats row (when keys exist)

3-column grid:

| Stat | Source |
|------|--------|
| Active Keys | Count where `IsActive` |
| Requests / 24h | Decorative static 1.2M |
| Avg Latency | Decorative static 14ms |

### Bento card grid

`GET /api/integration-keys` — responsive auto-fill grid (min 320px), one card per key.

| Element | Spec |
|---------|------|
| Header | Service icon + name + description subtitle + Active/Revoked pill |
| Service Key | Masked `pk_{CODE}_••••••••••••` in dark mono box + copy (info snackbar — key not recoverable) |
| Footer | Created date + Revoke button (active keys only) |

Revoked cards: dimmed, non-interactive.

### Generate Key flow

**Step 1 — `IntegrationKeyGenerateDialog`:** Service select + rotation warning. If active key exists, confirm rotate.

**Step 2 — `IntegrationKeyRevealDialog`:** One-time plain key reveal with copy, `X-Integration-Key` header note, “I've Saved It” dismiss. No backdrop dismiss.

`POST /api/integration-keys?serviceProductId={id}` → auto-revokes previous active key.

### Revoke Key

Confirm dialog with irreversible warning → `POST /api/integration-keys/{id}/revoke`.

### Bridge from Services

`/integration-keys?productId={id}` auto-opens generate dialog with service pre-selected.

### Empty state

Centered panel + Generate New Key CTA.

### Responsive

| xs | md+ |
|----|-----|
| Stats stack | Stats 3-col |
| Bento 1-col | Bento auto-fill |

### Components

`IntegrationKeys.razor.css`, `IntegrationKeyGenerateDialog`, `IntegrationKeyRevealDialog`, `ConfirmDialog`

### API

`GET /api/integration-keys`, `POST /api/integration-keys?serviceProductId={id}`, `POST /api/integration-keys/{id}/revoke`

---

## Audit Log `/audit`

**Purpose:** Read-only compliance trail of all admin actions.

Reference mock: Audit Log page (May 2026) — header search, expandable table, pagination footer, stats bento.

### Page header

| Element | Spec |
|---------|------|
| Title | H1 “Audit Log”, 32px `--primary` |
| Subtitle | “Comprehensive tracking of all administrative actions and security events.” |
| Search | Debounced header search field |
| Export | Outlined “Export CSV” button (filtered data) |

### Filters panel

Compact bar below header: Action select, Customer select, Refresh.

Query params: `customerId`, `licenseId` passed to API.

### Table

Custom expandable `<table>` in `.audit-table-panel` (not MudExpansionPanels).

| Column | Spec |
|--------|------|
| ☐ | Chevron — rotates 90° + primary when expanded |
| Timestamp | Mono `yyyy-MM-dd HH:mm:ss` |
| Admin | Avatar initials + email/name |
| Action | UPPER_SNAKE badge — primary / neutral / danger variants |
| Target | Customer name, license label, or invoice short id |
| IP Address | Mono or em dash |
| ⋮ | MoreVert menu — view customer/license/invoice, copy details |

### Expandable row

Click row toggles JSON panel:

- Header: `DETAILS_JSON` + Copy button
- `<pre>` formatted JSON in primary mono (`FormatJson` pretty-print)
- Background `--background` inset panel

### Pagination footer

Client-side on filtered results:

- Items per page: 10 / 25 / 50 / 100
- Range label + first/prev/page nums/next/last controls

### Stats bento (when data loaded)

4 cards below table:

| Stat | Source |
|------|--------|
| Total Logs | Filtered count |
| Security Flags | Revoke/suspend/void action count |
| Avg Retained | Static 90 Days |
| Storage Used | Estimated from loaded payload size |

### Audit actions (20)

`CustomerCreated`, `CustomerUpdated`, `CustomerSuspended`, `CustomerReactivated`,
`ServiceProductCreated`, `ServiceProductUpdated`,
`LicenseIssued`, `LicenseUpdated`, `LicenseActivated`, `LicenseRenewed`, `LicenseKeyRotated`,
`LicenseSuspended`, `LicenseRevoked`,
`IntegrationKeyCreated`, `IntegrationKeyRevoked`,
`InvoiceCreated`, `InvoiceSent`, `InvoiceVoided`, `ReceiptRecorded`, `InvoiceLinkedToLicense`

### Export

CSV of filtered rows: Timestamp, Action, PerformedBy, Target, IpAddress, Details.

### Responsive

| xs | md+ |
|----|-----|
| Header stacks | Header row with search + export |
| Stats 1-col | Stats 4-col |
| Horizontal scroll table | Full table |

### Components

`Audit.razor.css`, `MudTextField`, `MudSelect`, `MudMenu`, `MudIcon`

### API

`GET /api/audit-logs?limit=500&customerId=&licenseId=&action=`

---

## Validate License `/validate`

**Purpose:** Admin debug tool for testing `POST /api/licenses/validate`.

Reference mock: License Debugger page (May 2026) — split layout with terminal response panel.

**Canonical route:** `/validate`  
**Alias:** `/tools/validate` (same page or redirect)

### Page header

| Element | Spec |
|---------|------|
| Title | `<h1 class="page-title">` “Validate License” (matches nav label) |
| Subtitle | Low-level validation tool for license keys and integration keys |

### Layout

5:7 asymmetric grid: Input Parameters (left) + Response terminal (right).

### Form fields

| Field | Notes |
|-------|-------|
| License Key | Required mono textarea (8 lines) |
| Service Context | Optional product code select; auto-detect when empty |
| X-Integration-Key | Required, key-icon prefixed input |

### Response terminal

Black terminal panel with traffic-light dots, `response_log.json` label, Copy/Clear toolbar, pretty-printed JSON, status bar (Status + Latency).

| State | Status text |
|-------|-------------|
| Idle | Awaiting execution empty state |
| Loading | Processing… |
| Valid | 200 OK |
| Invalid | 403 Invalid |
| Error | Error |

### Info cards

Validation API card (endpoint + 60/min rate limit) + decorative validator behavior card.

### Components

`Validate.razor.css`, `MudForm`, `MudTextField`, `MudSelect`, `MudIcon`

### API

`POST /api/licenses/validate` — AllowAnonymous, rate-limited 60/min/IP. Header `X-Integration-Key`. Body `{ LicenseKey, ServiceCode? }`. Response `{ IsValid, PlanName?, ExpiresAt?, Message? }`.
