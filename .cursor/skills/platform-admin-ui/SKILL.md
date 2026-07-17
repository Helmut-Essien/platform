---
name: platform-admin-ui
description: >-
  Minimalist dark-theme MudBlazor UI/UX for the Platform admin hub (Blazor WASM).
  Dark MudBlazor admin UI — lime primary (#92d959), accent (#5c9f24), Inter + JetBrains Mono, as-built patterns.
  Use when building Client layouts, pages, dialogs, grids, or Phase 6 UI. Pair with
  platform-license-hub for API and domain rules.
---

# Platform Admin UI (MudBlazor)

## When to use this skill

- Implementing or styling anything under `Client/`
- Phase 6 Blazor admin dashboard
- Wireframe-aligned layouts, themes, accessibility

**Backend rules:** see `platform-license-hub` skill (entities, JWT, validation API, phases).

## Design identity

- **Minimalist dark** admin console inspired by [helmut-essien.github.io/portfolio](https://helmut-essien.github.io/portfolio/)
- **Primary:** `#92d959` — headings, active nav, primary CTAs
- **Accent:** `#5c9f24` — KPI values, keys, focus ring
- **Canvas:** `#10150c` page, `#1e1e1e` cards, `#191d14` drawer
- **Fonts:** Inter (UI), JetBrains Mono (labels, keys, JSON)
- **Framework:** Blazor WASM + MudBlazor v7+

Full tokens and MudTheme: [design-system.md](design-system.md).  
**As-built Client patterns:** [implementation-patterns.md](implementation-patterns.md) (page layout, splash, dialogs, filters).  
Navigation shell: [navigation-shell.md](navigation-shell.md).  
Screen specs: [screens.md](screens.md).  
Phase 1 wireframes: [wireframes-phase1.md](wireframes-phase1.md).  
Human-readable export: [docs/SaaS-Admin-Hub-UI-Spec.md](../../../docs/SaaS-Admin-Hub-UI-Spec.md).

**Viewport:** Mobile-ready, **desktop-optimized** (see design-system breakpoint cheat sheet).

## App shell (required pattern)

Replace default Bootstrap layout in `Client/Layout/` with:

- `MudLayout` + `MudThemeProvider` (dark palette from design-system)
- `MudAppBar` — 64px, `--bg-base`; **compact** (< 1024px: menu + brand + logout) or **desktop** (≥ 1024px: terminal logo badge, brand, user meta, logout). Search/notifications/account icons are **not** in the current build (see implementation-patterns).
- `MudDrawer` — see [navigation-shell.md](navigation-shell.md) for full three-tier responsive spec
- `MudMainContent` — `--bg-base`, padding `--container-margin` (24px) → 48px on lg+

### Sidebar summary (see navigation-shell.md)

| Viewport | Behavior |
|----------|----------|
| **≥ 1024px** (desktop) | Persistent **64px** icon rail; hover/focus-within → **280px** overlay; **profile** footer; desktop app bar |
| **768px – 1023px** (tablet) | Hamburger → **left** overlay **280px**; **Admin Menu** header; **profile** footer |
| **< 768px** (mobile) | Hamburger → **left** overlay **280px** (max 85vw); **Admin Menu** + close; **profile** footer (Logout in app bar) |

- `DrawerVariant.Mini`, `Breakpoint.Lg`, **`OpenMiniOnHover="false"`** — hover expand via CSS only at `nav-lg` (1024px)
- Surface `--surface-container-low`; transitions **300ms** `cubic-bezier(0.4, 0, 0.2, 1)`

### Navigation

| Route | Label | MudIcon |
|-------|-------|---------|
| `/` | Dashboard | Icons.Material.Filled.Dashboard |
| `/customers` | Customers | Icons.Material.Filled.Group |
| `/services` | Service Catalog | Icons.Material.Filled.Inventory2 |
| `/licenses` | Licenses | Icons.Material.Filled.VpnKey |
| `/invoices` | Invoices | Icons.Material.Filled.ReceiptLong |
| `/settings` | Settings | Icons.Material.Filled.Settings |
| `/integration-keys` | Integration Keys | Icons.Material.Filled.Key |
| `/audit` | Audit Log | Icons.Material.Filled.History |
| `/validate` | Validate License | Icons.Material.Filled.VerifiedUser |
| `/login` | (no drawer) | — |

Canonical route **`/validate`**. Alias **`/tools/validate`** may redirect to the same page (see screens.md).

Active nav: text `--primary`, **2px** left border **`--accent`** (`#5c9f24`), bg `--surface-container-highest`. Inactive: `--text-secondary`, hover `--on-surface` + `--surface-container`. Nav labels: JetBrains Mono 13px (desktop); hidden in collapsed rail until sidebar **hover or focus-within** (see navigation-shell.md).

## License status chips (`MudChip`)

All chips use `--accent` (`#5c9f24`). Differentiate by MudBlazor variant:

| Status | Variant | Enum |
|--------|---------|------|
| Active | Filled | `LicenseStatus.Active` |
| Pending | Outlined | `Pending` |
| Suspended | Outlined | `Suspended` |
| Revoked | Text | `Revoked` |
| Expired | Text | `Expired` |

Customer row: **Suspended** → Outlined; **Active** → Filled.

## UX rules (mandatory)

See also [design-system.md](design-system.md) for feedback states, destructive dialogs, snackbar styling, and accessibility.

1. **Destructive actions** (delete, revoke, suspend) → destructive confirm dialog per design-system (accent confirm, cancel default focus)
2. **Keys** — license/integration plain text shown **once** in modal; copy button; success glow `rgba(92,159,36,0.3)`; never list plain keys in grids
3. **Progressive disclosure** — summary in `MudDataGrid`; detail in drawer or expandable row
4. **Empty states** — illustration/message + primary CTA (`--primary` button, text `--on-primary`)
5. **Loading** — `MudSkeleton` on `--bg-surface`; avoid blank screens
6. **Errors** — inline `ErrorText` `--accent`; API errors → `MudSnackbar` or `MudAlert`
7. **Keyboard** — Tab order; Escape closes dialog; Enter submits primary form; focus per design-system
8. **Grids** — virtualize large datasets; dark header `--bg-surface`, row hover `--bg-elevated`
9. **Snackbar success** — `--accent` bg, `--bg-base` text (not MudBlazor default green)
10. **Motion** — honor `prefers-reduced-motion` globally (design-system)
11. **Delivery state** — business state and email state are separate; use `DeliveryTimeline` and never treat Invoice `Sent` or `LicenseKeySentAt` as provider confirmation
12. **Key replacement** — label the destructive operation “Rotate & Email Key”; never call it resend because the old key is invalidated

## UI implementation phases

All UI phases below are **implemented** in `Client/` (see [implementation-patterns.md](implementation-patterns.md)). Confirm with user before large new UI scope.

| UI phase | Scope | Status |
|----------|--------|--------|
| UI-1 | Shell + Dashboard | Done |
| UI-2 | Customers + Service catalog | Done |
| UI-3 | Licenses (+ `?customerId=`) | Done |
| UI-4 | Audit + Integration keys + Invoices | Done |
| UI-5 | Validate + Login | Done |

## Component cheat sheet

| Need | Implementation |
|------|----------------|
| Page title | `<h1 class="page-title">` + `<p class="page-subtitle">` in `app.css` |
| Page width | Root `class="*-page page-content"` — centered column (see implementation-patterns) |
| KPI cards | CSS grid + `.kpi-card` (Dashboard.razor.css) |
| Data tables | `MudDataGrid` (most pages), `MudTable` (Services), custom table (Audit) |
| Forms | `MudForm`, `MudTextField`, … in `MudDialog` |
| Dialogs | `MudDialogProvider` Medium + `PlatformDialogOptions` (Confirm / KeyReveal) |
| Boot loading | `.app-splash` in `index.html` (logo + green bar) |
| Mock metrics | `.demo-badge` label |
| Toast | `ISnackbar` — success: **`--accent` bg, `--bg-base` text** |
| Keys | `<code class="license-key">` + copy; one-time reveal dialog |

## Do not

- Use light MudBlazor default theme
- Put entities in Client — use `Platform.Shared` DTOs
- Use Bootstrap navbar from template scaffold
- Skip confirm on revoke/suspend/delete
- Store or display plain license keys outside one-time dialog

## Quick token reference

```
primary #92d959 | accent #5c9f24 | base #10150c | surface #1e1e1e | drawer #191d14
text-primary #ededed | text-secondary #a0a0a0 | border #2c2c2c
```
