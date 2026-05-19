---
name: platform-admin-ui
description: >-
  Dark-theme MudBlazor UI/UX for the Platform admin hub (Blazor WASM). Portfolio-inspired
  palette (#71e215 on black), Inter + JetBrains Mono, wireframes, and component specs.
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

- **Dark-first** admin console inspired by [helmut-essien.github.io/portfolio](https://helmut-essien.github.io/portfolio/)
- **Primary:** lime `#71e215` on black `#000000` — not indigo/teal
- **Fonts:** Inter (UI), JetBrains Mono (keys, JSON)
- **Framework:** Blazor WASM + MudBlazor v7+

Full tokens and MudTheme: [design-system.md](design-system.md).  
Screen specs: [screens.md](screens.md).  
Phase 1 shell + dashboard: [wireframes-phase1.md](wireframes-phase1.md).  
Human-readable export: [docs/SaaS-Admin-Hub-UI-Spec.md](../../../docs/SaaS-Admin-Hub-UI-Spec.md).

## App shell (required pattern)

Replace default Bootstrap layout in `Client/Layout/` with:

- `MudLayout` + `MudThemeProvider` (dark palette from design-system)
- `MudAppBar` — `#000000`, title "Platform Admin", user menu / logout
- `MudDrawer` — `#0c1408`, collapsible, nav from IA below
- `MudMainContent` — `#000000`, padding 24px

### Navigation

| Route | Label | MudIcon |
|-------|-------|---------|
| `/` | Dashboard | Icons.Material.Filled.Dashboard |
| `/customers` | Customers | Icons.Material.Filled.People |
| `/services` | Service Catalog | Icons.Material.Filled.Apps |
| `/licenses` | Licenses | Icons.Material.Filled.VpnKey |
| `/audit` | Audit Log | Icons.Material.Filled.History |
| `/tools/validate` | Validate License | Icons.Material.Filled.Science |
| `/login` | (no drawer) | — |

Active nav: text `#71e215`, bottom border `#5c9f24`. Inactive: `rgba(255,255,255,0.7)`, hover `#ffffff`.

## License status chips (`MudChip`)

| Status | Color | Enum |
|--------|-------|------|
| Active | `#71e215` | `LicenseStatus.Active` |
| Pending | `#FFCC00` | `Pending` |
| Suspended | `#f59e0b` | `Suspended` |
| Revoked | `#ef4444` | `Revoked` |
| Expired | `#64748b` | `Expired` |

Customer row: **Suspended** → chip `#f59e0b`; **Active** → `#71e215`.

## UX rules (mandatory)

1. **Destructive actions** (delete, revoke, suspend) → `MudDialog` confirm, error accent `#ef4444`
2. **Keys** — license/integration plain text shown **once** in modal; copy button; success glow `rgba(113,226,21,0.3)`; never list plain keys in grids
3. **Progressive disclosure** — summary in `MudDataGrid`; detail in drawer or expandable row
4. **Empty states** — illustration/message + primary CTA (`#71e215` button, text `#000000`)
5. **Loading** — `MudSkeleton` on `#0c1408`; avoid blank screens
6. **Errors** — inline `ErrorText` `#ef4444`; API errors → `MudSnackbar`
7. **Keyboard** — Tab order; Escape closes dialog; Enter submits primary form; focus `2px solid #FFCC00` offset 2px
8. **Grids** — virtualize large datasets; dark header `#0c1408`, row hover `#1a1a1a`

## UI implementation phases (deliver one at a time)

| UI phase | Scope |
|----------|--------|
| UI-1 | Shell + Dashboard ([wireframes-phase1.md](wireframes-phase1.md)) |
| UI-2 | Customers + Service catalog |
| UI-3 | Licenses (+ customer-scoped route) |
| UI-4 | Audit log + Integration keys |
| UI-5 | Validate tool + Login page |

Confirm with user before next UI phase. Match backend Phase 6 in `platform-license-hub/phases.md`.

## Component cheat sheet

| Need | MudBlazor |
|------|-----------|
| KPI cards | `MudGrid` + `MudCard` + `MudText` |
| Data tables | `MudDataGrid<T>` |
| Forms | `MudForm`, `MudTextField`, `MudAutocomplete`, `MudDatePicker` |
| Confirm | `MudDialog` / `DialogService.ShowMessageBox` |
| Toast | `ISnackbar` — success use `#71e215` bg, `#000000` text |
| Code/JSON | `MudCodeBlock` or `<pre class="text-code">` |
| Keys | `<code class="license-key">` + `MudIconButton` copy |

## Do not

- Use light MudBlazor default theme
- Put entities in Client — use `Platform.Shared` DTOs
- Use Bootstrap navbar from template scaffold
- Skip confirm on revoke/suspend/delete
- Store or display plain license keys outside one-time dialog

## Quick token reference

```
primary #71e215 | background #000000 | surface #0c1408 | body #FAF5E9
accent #FFCC00 | error #ef4444 | border #333333
```
