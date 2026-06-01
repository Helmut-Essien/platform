---
title: "Platform Admin UI — Implementation patterns (as-built)"
version: "1.0.0"
last_updated: "2026-06-01"
source: "Client/ Blazor WASM (MudBlazor 7.16)"
---

# Implementation patterns (as-built)

Canonical **code** reference for what is implemented today. Tokens and UX rules remain in [design-system.md](design-system.md). Per-screen behavior in [screens.md](screens.md).

**Key files:** `Client/wwwroot/css/app.css`, `Client/wwwroot/index.html`, `Client/App.razor`, `Client/Layout/`, `Client/Pages/`, `Client/Services/PlatformDialogOptions.cs`.

---

## Page layout

Every authenticated list/tool page wraps content in a root div with **`page-content`** plus a page-specific class (e.g. `customers-page page-content`).

| Class | File | Purpose |
|-------|------|---------|
| `.page-title` | `app.css` | H1 — Inter 2rem/600, color **`--primary`** (`#92d959`) |
| `.page-subtitle` | `app.css` | Subtitle under H1 — 0.875rem `--text-secondary` |
| `.page-content` | `app.css` | Centered column, `margin-inline: auto`, responsive max-width |
| `.page-title-drawer` | `app.css` | Drawer H2 — 1.25rem, `--primary` (customer detail) |
| `.invoice-detail-header` | `app.css` | Flex row: title + action buttons |

### `.page-content` breakpoints

| Viewport | `max-width` |
|----------|-------------|
| default | 100% |
| ≥ 960px | `min(100%, 96rem)` |
| ≥ 1280px | `min(100%, 100rem)` |
| ≥ 1920px | `min(100%, 112rem)` |

**Markup pattern:**

```html
<header class="customers-header">
  <div>
    <h1 class="page-title">Customers</h1>
    <p class="page-subtitle">…</p>
  </div>
  …
</header>
```

Do **not** set per-page `h1` color in scoped CSS — use global `.page-title`.

Empty-state `h2` stays **`--text-primary`** (white), subordinate to the green page title.

---

## WASM boot splash

Before Blazor replaces `#app`, [`index.html`](../../../Client/wwwroot/index.html) shows:

| Element | Spec |
|---------|------|
| Logo | `images/login-logo.png`, 5.5rem, `border-radius: 1.25rem`, login-matching shadow; `onerror` → `favicon.png` |
| Progress | 12rem × 3px track; fill width = `calc(var(--blazor-load-percentage, 0) * 1%)`; gradient `--accent` → `--primary`; shimmer animation |
| Status | JetBrains Mono 0.75rem uppercase; `var(--blazor-load-percentage-text)` |
| Canvas | `#app` full viewport, `--bg-base`; `theme-color` `#10150c` |

Respects `prefers-reduced-motion` (no shimmer / width transition).

**Do not** use the default Blazor SVG circle loader.

---

## Dialogs

### Global provider ([`App.razor`](../../../Client/App.razor))

```razor
<MudDialogProvider MaxWidth="MaxWidth.Medium" FullWidth="true" />
```

Default for create/edit forms: **~960px** max, full width within cap.

### [`PlatformDialogOptions`](../../../Client/Services/PlatformDialogOptions.cs)

| Preset | `MaxWidth` | `FullWidth` | Notes |
|--------|------------|-------------|--------|
| `Form` | Medium | true | Same as provider default; use when passing explicit options |
| `Confirm` | Small | true | All `ConfirmDialog` opens |
| `KeyReveal` | Small | true | `CloseOnEscapeKey` / `BackdropClick` false |

```csharp
await DialogService.ShowAsync<ConfirmDialog>(…, parameters, PlatformDialogOptions.Confirm);
```

`PageHeader.razor` was removed — detail pages use native `<h1 class="page-title">`.

---

## Filter selects (`MudSelect`)

Use an explicit **`"all"`** sentinel for “no filter” — **not** empty string `""`.

| Page | Control | Default | “All” value |
|------|---------|---------|-------------|
| Customers | Status | All Statuses | `"all"` |
| Customers | Created | All Time | `"all"` |
| Invoices | (quick chips) | ALL | `"all"` |

Empty string + `Clearable` on MudSelect causes blank/wrong display on load (label float without value).

**Pattern:**

```razor
<MudSelect T="string" Value="_statusFilter" ValueChanged="OnStatusFilterChanged" …>
  <MudSelectItem Value="@("all")">All Statuses</MudSelectItem>
  …
</MudSelect>
```

```csharp
private string _statusFilter = "all";
// filter: if (_statusFilter == "active") …
```

Licenses/Invoices use **nullable enums** with `null` = all (also valid).

---

## Status display

| Context | Implementation |
|---------|----------------|
| License grid | Custom `.license-status-badge` + `LicenseStatusChip.razor` (MudChip) available |
| Customer grid / drawer | Custom `.status-badge` / `.status-badge-active` / `.status-badge-suspended` in `Customers.razor.css` |
| Design intent | Active: lime tint + border; Suspended: elevated gray (see design-system customer row note) |

---

## Demo / mock metrics

Non-API metrics are **kept** but labeled with **`.demo-badge`** (“Demo data”):

- Dashboard — Platform Health (uptime, sync queue)
- Integration Keys — Requests/24h, Avg Latency
- Services — System Uptime, Service Performance Matrix chart
- Invoices — “+12.5% vs last month”, Automated Billing Rules panel

Do not present these as live API data without removing the badge or wiring real endpoints.

---

## Data tables (per page)

| Page | Component |
|------|-----------|
| Customers, Licenses, Invoices | `MudDataGrid<T>` + scoped `::*deep` grid CSS |
| Services | `MudTable` |
| Audit | Custom `<table class="audit-table">` + client pagination |
| Invoice detail | `MudTable` (receipts) |

Unifying on `MudDataGrid` is optional future work.

---

## App bar (implemented vs deferred)

| Element | Desktop (≥1024px) | Compact (<1024px) |
|---------|-------------------|-------------------|
| Menu / hamburger | Hidden | Visible |
| Terminal logo badge + brand | Yes | Brand only |
| User name + “Superuser” | Yes | No |
| Logout | Yes | Yes |
| Cmd+K search | **Not implemented** (removed) | — |
| Notifications | **Not implemented** | — |
| Account icon | **Not implemented** | — |

Future search/notifications should re-use design tokens from [navigation-shell.md](navigation-shell.md) when built.

---

## Brand assets

| Asset | Path |
|-------|------|
| Login + splash logo | `wwwroot/images/login-logo.png` |
| Favicon / splash fallback | `wwwroot/favicon.png` |
| App title | `HelmutCode — Platform Admin` (`index.html`) |

Login logo: `border-radius: 1.25rem` (`.login-logo` / `.app-splash-logo`).

---

## UI delivery status

| UI phase | Scope | Status |
|----------|--------|--------|
| UI-1 | Shell + Dashboard | Implemented |
| UI-2 | Customers + Services | Implemented |
| UI-3 | Licenses | Implemented |
| UI-4 | Audit + Integration keys + Invoices | Implemented |
| UI-5 | Validate + Login | Implemented |

Known gaps (documented, not blocking): audit `?highlight=` deep links; dashboard expiring-licenses filter; optional shell search.
