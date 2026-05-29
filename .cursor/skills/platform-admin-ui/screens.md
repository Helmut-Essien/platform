# Screen specifications

All screens use [design-system.md](design-system.md) tokens. Responsive behavior per [wireframes-phase1.md](wireframes-phase1.md). Layout: `MainLayout` shell or `LoginLayout` for `/login`.

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
- Snackbar for transient success/failure after API call: `Snackbar.Add("Saved", Severity.Success)`.

### Keyboard + focus

- Enter key submits the primary form action (`OnKeyUp` handler checks `e.Key == "Enter"`).
- Tab order follows visual field order.
- Focus ring: `2px solid --accent` offset 2px on all interactive elements.
- Escape closes dialogs (`MudDialog` handles this by default).

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
| Submit | `MudButton` Variant.Filled | `--accent` bg, `--bg-base` text, full width, min-height 3rem, border-radius 0.75rem, font-weight 600 |
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

See [wireframes-phase1.md](wireframes-phase1.md) for full layout.

### KPIs

`GET /api/dashboard/stats` → `{ CustomerCount, ActiveLicenses, ExpiringWithin30Days, UnpaidInvoices }`

| KPI card | Click-through |
|----------|---------------|
| Total Customers | → `/customers` |
| Active Licenses | → `/licenses` |
| Expiring Within 30 Days | → `/licenses` (pre-filtered expiring) |
| Unpaid Invoices | → `/invoices` (filtered Sent + PartiallyPaid + Overdue) |

### Quick actions

| Button | Target |
|--------|--------|
| + New Customer | `/customers?add=true` |
| Issue License | `/licenses?add=true` |
| Generate Integration Key | `/integration-keys` |

### Recent activity timeline

`GET /api/audit-logs?limit=10` → `MudTimeline` with last 10 events. "View full audit →" links to `/audit`.

### Components

`MudGrid`, `MudCard`, `MudText`, `MudStack`, `MudButton`, `MudTimeline`, `MudTimelineItem`, `MudSkeleton`, `MudAlert`

---

## Customers `/customers`

**Purpose:** Manage organizations. Entry point to customer licenses, invoices, and audit history.

### Grid

`GET /api/customers?page=1&pageSize=25`

| Column | Notes |
|--------|-------|
| Name | Link → opens detail drawer (Profile tab) |
| Contact Email | |
| Contact Phone | |
| Status | `MudChip` — **Active**: Filled `--accent`, **Suspended**: Outlined `--accent` |
| License Count | |
| Created At | Sortable |
| Actions | `MudIconButton` → `MudMenu`: Edit · View Licenses · Suspend/Reactivate · Delete |

### Filters

Search `MudTextField`, Status `MudSelect` (All / Active / Suspended), Date range `MudDateRangePicker`. Collapsed in `MudExpansionPanel` on xs/sm, inline on md+.

### Row actions

| Action | UX |
|--------|-----|
| Edit | `MudDialog` — fields: Name, ContactEmail, ContactPhone, InternalNotes. Follows [Form patterns](#form-patterns). `PUT /api/customers/{id}` |
| View Licenses | Navigate `/licenses?customerId={id}` |
| Suspend | Confirm dialog: all licenses denied in Redis. `POST /api/customers/{id}/suspend` |
| Reactivate | (on suspended rows) Confirm dialog: clears deny-list. `POST /api/customers/{id}/reactivate` |
| Delete | Confirm + type customer name to confirm. Button `--accent` |

### Create customer

Follows [Form patterns](#form-patterns).

`[+ New Customer]` → `MudDialog` with Name (required), ContactEmail (required, email), ContactPhone, InternalNotes. `POST /api/customers` → 201.

### Detail drawer (`MudDrawer` Anchor.Right, width 480px md+ / half-screen sm / full-screen xs bottom sheet)

| Tab | Content |
|-----|---------|
| **Profile** | All fields + InternalNotes `MudTextField` multiline + Edit button |
| **Licenses** | `GET /api/licenses?customerId={id}` — same grid as /licenses, scoped |
| **Invoices** | `GET /api/invoices?customerId={id}` — same grid as /invoices, scoped |
| **Audit** | `GET /api/audit-logs?customerId={id}` — filtered audit entries |

### Empty state

"No customers yet" + `[+ New Customer]` CTA button.

### Responsive

| xs (<600px) | sm (600–959px) | md+ (≥960px) |
|-------------|----------------|--------------|
| Card list, 1 item/row | Card list, 2 items/row | `MudDataGrid` full columns |
| Filters in panel | Filters in panel | Filters inline bar |
| Drawer: bottom sheet | Drawer: half-width right | Drawer: 480px right |

### Components

`MudDataGrid`, `MudChip`, `MudDialog`, `MudTextField`, `MudDateRangePicker`, `MudMenu`, `MudIconButton`, `MudDrawer`, `MudTabs`, `MudExpansionPanel`

### API

`GET/POST /api/customers`, `PUT /api/customers/{id}`, `POST /api/customers/{id}/suspend`, `POST /api/customers/{id}/reactivate`

---

## Service Catalog `/services`

**Purpose:** CRUD `ServiceProduct`. Entry point to integration keys.

### Grid

`GET /api/serviceproducts`

| Column | Notes |
|--------|-------|
| Name | |
| Code | Unique, readonly after create |
| Description | |
| Available | `MudSwitch` inline — thumb `--accent`, track `--border-subtle`. Calls `PUT /api/serviceproducts/{id}` |
| Int. Key | Chip — **Active**: Outlined `--accent`, **None**: Text `--text-secondary`. Click → `/integration-keys?serviceProductId={id}` |
| Actions | Edit · View Keys |

### Add/Edit service dialog

Follows [Form patterns](#form-patterns).

| Field | Component | Notes |
|-------|-----------|-------|
| Name | `MudTextField` | Required, max 200 |
| Code | `MudTextField` | Required, max 50, **readonly after create** |
| Description | `MudTextField` multiline | Optional, max 2000 |
| Available for sale | `MudSwitch` | Default true |

### Post-create integration key prompt

After `POST /api/serviceproducts` → 201, show inline prompt:

```
┌──────────────────────────────────────────┐
│ ✓ Service "{Name}" created               │
│                                          │
│ An integration key is required for       │
│ external apps to validate licenses for   │
│ this service.                            │
│                                          │
│ [Generate Integration Key]  [Skip]       │
└──────────────────────────────────────────┘
```

**[Generate Integration Key]** → `POST /api/integration-keys?serviceProductId={id}` → one-time key reveal dialog (see Integration Keys section).

**[Skip]** → closes prompt. Service shows "Int.Key: None". Admin can generate later via `/integration-keys`.

### Security notice

`MudAlert` Severity.Warning, border-left `--accent`: "Integration keys cannot be recovered after generation. Store them securely."

### Responsive

| xs (<600px) | sm (600–959px) | md+ (≥960px) |
|-------------|----------------|--------------|
| Card list | Card list | `MudDataGrid` |
| Create dialog: bottom sheet | Centered 420px | Centered 480px |
| Post-create prompt: bottom sheet | Centered 400px | Centered 440px |

### Components

`MudDataGrid`, `MudSwitch`, `MudChip`, `MudDialog`, `MudTextField`, `MudAlert`, `MudIconButton`

### API

`GET/POST /api/serviceproducts`, `PUT /api/serviceproducts/{id}`, `POST /api/integration-keys?serviceProductId={id}`

---

## Licenses `/licenses`

**Purpose:** Global license lifecycle — issue, activate, renew, suspend, revoke.

### Grid

`GET /api/licenses?page=1&pageSize=25&customerId=&includeSuspendedCustomers=false`

| Column | Notes |
|--------|-------|
| Customer | |
| Service | Service product code |
| Plan | Plan name |
| Status | Chip — see table below |
| Expires | ExpiresAt date |
| Key Sent | Chip — **Sent**: Outlined `--accent`, **—**: Text `--text-secondary` |
| Actions | Activate · Renew · Suspend · Revoke · Update |

### Filters

Search, Customer `MudAutocomplete` (async), Service `MudSelect`, Status `MudSelect`, Date range `MudDateRangePicker`. Bulk: `[Suspend]` `[Renew]` appear when rows selected.

### Status chips

All chips use `--accent`. Differentiate by MudBlazor variant:

| Status | Variant | Available transitions |
|--------|---------|----------------------|
| Pending | Outlined | Activate |
| Active | **Filled** | Suspend · Renew · Revoke · Update |
| Suspended | Outlined | Activate · Revoke |
| Revoked | Text | (none — permanent) |
| Expired | Text | Activate · Renew |

### Issue License modal

Follows [Form patterns](#form-patterns).

`POST /api/licenses` → 201, status = Pending.

| Field | Component | Notes |
|-------|-----------|-------|
| Customer | `MudAutocomplete` | Async search `GET /api/customers`, required |
| Service | `MudSelect` | HOSTEL · LAUNDRY · SCHOOL · ASSET, required |
| Plan Name | `MudTextField` | Required, max 100 |
| Expires At | `MudDatePicker` | Optional |
| Notes | `MudTextField` | Optional |

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

Navigate `/licenses?customerId={id}` from customer detail drawer or row action. Grid pre-filters by customer. Breadcrumb: Customers > {Name} > Licenses.

### Responsive

| xs (<600px) | sm (600–959px) | md+ (≥960px) |
|-------------|----------------|--------------|
| Card list | Card list | `MudDataGrid` multi-select |
| Issue/Activate: bottom sheet | Centered 440px | Centered 520px |
| Key reveal: full-screen | Centered 420px | Centered 480px |

### Components

`MudDataGrid` multi-select, `MudChip`, `MudAutocomplete`, `MudSelect`, `MudTextField`, `MudDatePicker`, `MudDialog`, `MudBadge`, `MudIconButton`, `MudSnackbar`

### API

`GET/POST /api/licenses`, `POST /activate`, `POST /renew`, `POST /suspend`, `POST /revoke`, `PUT /api/licenses/{id}`

---

## Invoices `/invoices`

**Purpose:** List and manage bills. Record payments.

### Grid

`GET /api/invoices?page=1&pageSize=25&customerId=`

| Column | Notes |
|--------|-------|
| Invoice # | Format `INV-{year}-{00000..}`, link → `/invoices/{id}` |
| Customer | |
| Total | TotalAmount |
| Paid | AmountPaid (sum of receipts) |
| Due | AmountDue (Total − Paid) |
| Status | Chip — see below |
| Issue Date | |
| Actions | |

### Status chips

All chips use `--accent`. Differentiate by variant:

| Status | Variant |
|--------|---------|
| Draft | Text |
| Sent | Outlined |
| PartiallyPaid | Outlined |
| Paid | **Filled** |
| Overdue | **Filled** |
| Void | Text |

### Filters

Customer `MudAutocomplete`, Status `MudSelect`, Date range `MudDateRangePicker`.

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

### Grid

`GET /api/integration-keys?serviceProductId=`

| Column | Notes |
|--------|-------|
| Service | Service product name + code |
| Masked Preview | `<code>` — `pk_HOSTEL_••••••••••••`. Never show full hash. |
| Active | `MudChip` — **Active**: Filled `--accent`, **Revoked**: Text `--text-secondary` |
| Created | |
| Last Used | |
| Actions | Revoke |

### Filter

Service product `MudSelect` to scope the grid to one product.

### Security notice

`MudAlert` Severity.Warning, border-left `--accent`: "Integration keys cannot be recovered after generation. Store them securely."

### Generate Key flow

Follows [Form patterns](#form-patterns).

**Step 1 — Select service:**

`[Generate Key]` → `MudDialog`.

| Field | Component | Notes |
|-------|-----------|-------|
| Service | `MudSelect` | Required |

Warning: "Creating a new key will revoke the previous active key for this service. Any app using the old key will fail validation."

`POST /api/integration-keys?serviceProductId={id}` → Returns `{ Key: IntegrationKeyDto, PlainKey }`.

**Step 2 — One-time key reveal:**

- Dialog title: "Integration Key Created"
- Service name + code
- `<code class="license-key">` displays plain key (JetBrains Mono, same styling as license key reveal)
- `MudIconButton` copy → Snackbar "Copied"
- Warning: "Store this key securely. It will never be displayed again. Any app validating licenses for {service} must send this key in the X-Integration-Key header."
- `[I've Saved It]` button dismisses. No dismiss-by-click-outside. No close X.

API auto-revokes previous active key in transaction. Audit logs `IntegrationKeyCreated` + `IntegrationKeyRevoked` for old key.

### Revoke Key

Confirm dialog: "Revoke integration key for {Service}? External apps using this key will fail validation. This cannot be undone."

`POST /api/integration-keys/{id}/revoke`. Audit logs `IntegrationKeyRevoked`.

### Bridge from Services

When a service is created without a key, navigate here via `/integration-keys?serviceProductId={id}`. The post-create prompt from the Services page also flows directly here — the service product is pre-selected, so the dialog skips straight to the one-time key reveal.

### Responsive

| xs (<600px) | sm (600–959px) | md+ (≥960px) |
|-------------|----------------|--------------|
| Card list | Card list | `MudDataGrid` |
| Generate: bottom sheet | Centered 400px | Centered 480px |
| Key reveal: full-screen | Centered 420px | Centered 480px |

### Components

`MudDataGrid`, `MudChip`, `MudSelect`, `MudDialog`, `MudAlert`, `MudIconButton`, `MudSnackbar`

### API

`GET /api/integration-keys`, `POST /api/integration-keys?serviceProductId={id}`, `POST /api/integration-keys/{id}/revoke`

---

## Audit Log `/audit`

**Purpose:** Read-only compliance trail of all admin actions.

### Grid

`GET /api/audit-logs?limit=100&customerId=&licenseId=&invoiceId=&action=`

| Column | Notes |
|--------|-------|
| Timestamp | Sortable, descending default |
| Admin | PerformedBy (from JWT name claim) |
| Action | Action name — see list below |
| Target | Customer / License / Invoice link where available |
| IP | IpAddress |

### Expandable row

Click to expand → `MudCodeBlock` or `<pre class="text-code">` with formatted `DetailsJson`:
- Font: JetBrains Mono, 0.8125rem
- Background: `--bg-elevated`
- Text: `--text-primary`
- Border: `1px solid --border-subtle`

### Filters

| Filter | Component | Notes |
|--------|-----------|-------|
| Action | `MudSelect` | 25 audit actions |
| Customer | `MudAutocomplete` | Scope to one customer |
| License | | Query param only (linked from license detail) |
| Invoice | | Query param only (linked from invoice detail) |
| Date range | `MudDateRangePicker` | |
| Admin | `MudTextField` | Search by performer name |

### Audit actions (25)

`CustomerCreated`, `CustomerUpdated`, `CustomerSuspended`, `CustomerReactivated`,
`ServiceProductCreated`, `ServiceProductUpdated`,
`LicenseIssued`, `LicenseUpdated`, `LicenseActivated`, `LicenseRenewed`, `LicenseKeyRotated`,
`LicenseSuspended`, `LicenseRevoked`,
`IntegrationKeyCreated`, `IntegrationKeyRevoked`,
`InvoiceCreated`, `InvoiceSent`, `InvoiceVoided`, `ReceiptRecorded`, `InvoiceLinkedToLicense`

### Export

`[Export CSV]` button → downloads currently filtered data as CSV. Client-side from visible grid data.

### Responsive

| xs (<600px) | sm (600–959px) | md+ (≥960px) |
|-------------|----------------|--------------|
| Card list, JSON collapsed | Card list, JSON expandable | `MudDataGrid`, JSON in expand row |
| Filters in panel | Filters in panel | Filters inline bar |
| Export button: full width | Auto width | Auto width |

### Components

`MudDataGrid`, `MudSelect`, `MudAutocomplete`, `MudDateRangePicker`, `MudTextField`, `MudCodeBlock`, `MudButton`, `MudExpansionPanel`

### API

`GET /api/audit-logs` with query filters: `customerId`, `licenseId`, `invoiceId`, `action`, `limit` (1–500, default 100)

---

## Validate License `/tools/validate`

**Purpose:** Admin debug tool for testing `POST /api/licenses/validate`.

### Form

Follows [Form patterns](#form-patterns).

| Field | Component | Notes |
|-------|-----------|-------|
| License Key | `MudTextField` multiline | 6 rows, JetBrains Mono, required |
| Service | `MudSelect` | Optional — HOSTEL · LAUNDRY · SCHOOL · ASSET. Auto-detect if blank. |
| X-Integration-Key | `MudTextField` | Required, label "X-Integration-Key header" |

Info text: "Sends to `POST /api/licenses/validate` with the `X-Integration-Key` header. Rate limited to 60 requests per minute."

### Test button

`MudButton` Variant.Filled `--accent`. `[Test Validation]`.

### Response panel

`MudExpansionPanel` "Response" — shows after API call.

| Result | Display |
|--------|---------|
| Loading | `MudProgressLinear` indeterminate |
| Valid | Green `--accent`: `{ "isValid": true, "planName": "Pro Annual", "expiresAt": "2027-05-15" }` in JetBrains Mono |
| Invalid | `--text-secondary`: `{ "isValid": false, "message": "License key not found or expired." }` |
| Integration key invalid | `{ "isValid": false, "message": "Invalid integration key." }` |
| Rate limited (429) | `MudAlert` Severity.Error: "Too many requests. Try again later." |

### Components

`MudTextField`, `MudSelect`, `MudButton`, `MudExpansionPanel`, `MudProgressLinear`, `MudAlert`

### API

`POST /api/licenses/validate` — AllowAnonymous, rate-limited 60/min/IP. Header `X-Integration-Key`. Body `{ LicenseKey, ServiceCode? }`. Response `{ IsValid, PlanName?, ExpiresAt?, Message? }`.
