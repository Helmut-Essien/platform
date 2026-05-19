# Screen specifications

All screens use [design-system.md](design-system.md) tokens and `AdminLayout` shell unless `/login`.

---

## Login `/login`

**Purpose:** Authenticate admin; obtain JWT for API.

| Element | Spec |
|---------|------|
| Layout | Centered `MudPaper` on `#000000`, max-width 400px, surface `#0c1408`, border `#333` |
| Fields | Email/username, password — `MudTextField`, dark variant |
| Submit | `btn-action` full width |
| Errors | `MudAlert` Severity.Error |
| Success | Redirect to `/` |

No sidebar. No plain keys.

---

## Dashboard `/`

See [wireframes-phase1.md](wireframes-phase1.md).

---

## Customers `/customers`

**Purpose:** Manage organizations; entry point to customer licenses.

### Layout

```
[H1 Customers]                    [ + New Customer ]
[ Search________ ] [Status v] [Date from] [Date to]
+------------------------------------------------------------------+
| MudDataGrid                                                      |
| Name | Email | Phone | Status | Lic# | Created | Actions         |
+------------------------------------------------------------------+
```

### Grid columns

| Column | Notes |
|--------|-------|
| Name | Link → detail drawer |
| Contact Email | |
| Contact Phone | |
| Status | `MudChip` Active `#71e215` / Suspended `#f59e0b` |
| License Count | Number |
| Created At | Sortable |
| Actions | Icon menu: Edit, View Licenses, Suspend/Activate, Delete |

### Row actions

| Action | UX |
|--------|-----|
| Edit | `MudDialog` form |
| View Licenses | Navigate `/customers/{id}/licenses` |
| Suspend | Confirm dialog `#ef4444` |
| Delete | Confirm + type customer name (optional) |

### Detail drawer (`MudDrawer` Anchor.Right)

- Tabs: Profile | Licenses | Audit (filtered)
- Profile: all customer fields + InternalNotes `MudTextField` multiline

### Empty state

"No customers yet" + **New Customer** CTA.

### Components

`MudDataGrid`, `MudChip`, `MudDialog`, `MudTextField`, `MudDateRangePicker`, `MudMenu`, `MudIconButton`

---

## Service catalog `/services`

**Purpose:** CRUD `ServiceProduct`; entry to integration keys.

### Layout

```
[H1 Service Catalog]              [ + Add Service ]
+------------------------------------------------------------------+
| Name | Code | Description | Available | Int.Key | Actions       |
+------------------------------------------------------------------+
```

| Column | Notes |
|--------|-------|
| Available | `MudSwitch` thumb `#71e215`, track `#333` |
| Int. Key | Chip Active/None; click → `/services/{id}/keys` |
| Actions | Edit, View Keys |

### Edit modal

Name, Code (readonly after create), Description, `IsAvailableForSale` switch.

### Security copy

`MudAlert` Severity.Warning, border-left `#FFCC00`: integration keys cannot be recovered after generation.

---

## Licenses `/licenses`

**Purpose:** Global license lifecycle view.

### Layout

```
[H1 Licenses]                     [ Issue License ]
[Search] [Customer v] [Service v] [Status v]
[ Bulk: Suspend ] [ Bulk: Renew ]
+------------------------------------------------------------------+
| Customer | Service | Plan | Status | Expires | Key Sent | ...  |
+------------------------------------------------------------------+
```

### Status chips

| Status | Color |
|--------|-------|
| Active | `#71e215` |
| Suspended | `#f59e0b` |
| Revoked | `#ef4444` |
| Expired | `#64748b` |
| Pending | `#FFCC00` |

### Issue license modal

1. `MudAutocomplete` customer (async search)
2. `MudSelect` service (HOSTEL, LAUNDRY, SCHOOL, ASSET)
3. Plan name `MudTextField`
4. `MudDatePicker` expiry
5. Notes (optional)

**On success (Phase 4 backend):** second step dialog — show `.license-key` + "Email sent" + Copy + Done.

### Bulk actions

Selected rows → Suspend confirm / Renew with shared expiry picker.

### Components

`MudDataGrid` multi-select, `MudBadge`, `MudDatePicker`, `MudSnackbar`

---

## Customer licenses `/customers/{id}/licenses`

Same grid as global licenses, pre-filtered; breadcrumb: Customers > {Name} > Licenses.

---

## Audit log `/audit`

**Purpose:** Read-only compliance trail.

### Layout

```
[H1 Audit Log]                              [ Export CSV ]
[Action v] [Target type v] [Date range] [Search admin]
+------------------------------------------------------------------+
| > | Timestamp | Admin | Action | Target | IP                   |
+------------------------------------------------------------------+
```

### Expandable row

`MudCodeBlock` or `<pre class="text-code">` for `DetailsJson` — JetBrains Mono, bg `#1a1a1a`.

### Export

Download filtered CSV (client-side or API — follow backend phase).

---

## Integration keys `/services/{id}/keys`

**Purpose:** One active key per product; rotate with one-time reveal.

### Layout

```
Breadcrumb: Services > {Name} > Integration Key
[MudAlert Warning] Store this key securely — it cannot be retrieved again.

+------------------------------------------+
| Masked hash preview  ••••••••••••        |
| Created | Last Used | Status Active      |
| [ Regenerate Key ]  (destructive confirm)|
+------------------------------------------+
```

### Regenerate flow

1. Confirm `#ef4444`
2. Show plain key in dialog — `.license-key` + Copy — **single display**
3. Snackbar success

Never show full hash; masked preview only.

---

## Validate license `/tools/validate`

**Purpose:** Admin debug tool for `POST /api/licenses/validate` (Phase 5 API).

### Layout

```
[H1 Validate License]
[MudTextField multiline] License Key
[MudSelect] Service (optional auto-detect)
[ Test Validation ]

MudExpandPanel "Response"
  JSON with IsValid color: green #71e215 / red #ef4444
```

Uses `HttpClient` to real API — not a mock. Header `X-Integration-Key` from secure admin-only server proxy or manual field in dev.

---

## Cross-screen patterns

| Pattern | Rule |
|---------|------|
| Page header | `MudText` h4 + primary action right-aligned |
| Filters | Collapse on mobile into `MudExpansionPanel` |
| Pagination | Server-side when API supports it |
| Copy | `MudIconButton` Icons.ContentCopy + Snackbar "Copied" |
| 401 | Redirect `/login` |
| 403 | `MudAlert` + link home |
