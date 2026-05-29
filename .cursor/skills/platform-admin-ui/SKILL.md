---
name: platform-admin-ui
description: >-
  Minimalist dark-theme MudBlazor UI/UX for the Platform admin hub (Blazor WASM).
  Single lime accent (#5c9f24), Inter + JetBrains Mono, wireframes, and component specs.
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
- **Single accent:** `#5c9f24` — no secondary colors, no semantic palette
- **Fonts:** Inter (UI), JetBrains Mono (keys, JSON)
- **Framework:** Blazor WASM + MudBlazor v7+

Full tokens and MudTheme: [design-system.md](design-system.md).  
Screen specs: [screens.md](screens.md).  
Phase 1 shell + dashboard: [wireframes-phase1.md](wireframes-phase1.md).  
Human-readable export: [docs/SaaS-Admin-Hub-UI-Spec.md](../../../docs/SaaS-Admin-Hub-UI-Spec.md).

## App shell (required pattern)

Replace default Bootstrap layout in `Client/Layout/` with:

- `MudLayout` + `MudThemeProvider` (dark palette from design-system)
- `MudAppBar` — `var(--bg-base)`, title "Platform Admin", user menu / logout
- `MudDrawer` — `var(--bg-surface)`, collapsible, nav from IA below
- `MudMainContent` — `var(--bg-base)`, padding 24px

### Navigation

| Route | Label | MudIcon |
|-------|-------|---------|
| `/` | Dashboard | Icons.Material.Filled.Dashboard |
| `/customers` | Customers | Icons.Material.Filled.People |
| `/services` | Service Catalog | Icons.Material.Filled.Apps |
| `/licenses` | Licenses | Icons.Material.Filled.VpnKey |
| `/invoices` | Invoices | Icons.Material.Filled.Receipt |
| `/audit` | Audit Log | Icons.Material.Filled.History |
| `/tools/validate` | Validate License | Icons.Material.Filled.Science |
| `/login` | (no drawer) | — |

Active nav: text `--accent`, bottom border `--accent-active`. Inactive: `--text-secondary`, hover `--text-primary`.

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

1. **Destructive actions** (delete, revoke, suspend) → `MudDialog` confirm, accent `--accent`
2. **Keys** — license/integration plain text shown **once** in modal; copy button; success glow `rgba(92,159,36,0.3)`; never list plain keys in grids
3. **Progressive disclosure** — summary in `MudDataGrid`; detail in drawer or expandable row
4. **Empty states** — illustration/message + primary CTA (`--accent` button, text `--bg-base`)
5. **Loading** — `MudSkeleton` on `--bg-surface`; avoid blank screens
6. **Errors** — inline `ErrorText` `--accent`; API errors → `MudSnackbar`
7. **Keyboard** — Tab order; Escape closes dialog; Enter submits primary form; focus `2px solid --accent` offset 2px
8. **Grids** — virtualize large datasets; dark header `--bg-surface`, row hover `--bg-elevated`

## UI implementation phases (deliver one at a time)

| UI phase | Scope |
|----------|--------|
| UI-1 | Shell + Dashboard ([wireframes-phase1.md](wireframes-phase1.md)) |
| UI-2 | Customers + Service catalog |
| UI-3 | Licenses (+ customer-scoped route) |
| UI-4 | Audit log + Integration keys + Invoices/Receipts |
| UI-5 | Validate tool + Login page |

Confirm with user before next UI phase. Match backend Phase 6 in `platform-license-hub/phases.md`.

## Component cheat sheet

| Need | MudBlazor |
|------|-----------|
| KPI cards | `MudGrid` + `MudCard` + `MudText` |
| Data tables | `MudDataGrid<T>` |
| Forms | `MudForm`, `MudTextField`, `MudAutocomplete`, `MudDatePicker` |
| Confirm | `MudDialog` / `DialogService.ShowMessageBox` |
| Toast | `ISnackbar` — success use `--accent` bg, `--bg-base` text |
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
accent #5c9f24 | base #121212 | surface #1e1e1e | elevated #2a2a2a
text-primary #ededed | text-secondary #a0a0a0 | border #2c2c2c
```
