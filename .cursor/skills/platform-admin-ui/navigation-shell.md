---
title: "Platform Admin — Responsive Navigation Shell"
version: "2.2.0"
source: "Stitch Navigation Responsive Behavior (Jun 2026); aligned to Client build Jun 2026"
last_updated: "2026-06-01"
---

# Responsive navigation shell

Canonical reference for the **app shell** (`Client/Layout/MainLayout.razor`, `NavMenu.razor`).  
Page content breakpoints (grids, DataGrid) may use MudBlazor `md` (960px); **navigation** uses **`nav-lg` = 1024px** for rail vs overlay, and **`nav-md` = 768px** to split mobile vs tablet overlay styling.

---

## 1. Viewport modes

| Mode | CSS width | Shell behavior |
|------|-----------|----------------|
| **Mobile overlay** | `< 768px` | Hamburger → **left** overlay **280px** (max **85vw**); **Admin Menu** header + **close**; **profile** footer; Logout in app bar only |
| **Tablet overlay** | `768px – 1023px` | Hamburger → **left** overlay **280px**; **Admin Menu** header (no close); **profile** footer |
| **Desktop mini-drawer** | `≥ 1024px` | Persistent **64px** icon rail; hover/focus-within → **280px** overlay; **profile** footer; desktop app bar |

### Breakpoint tokens

| Token | Value | Used for |
|-------|-------|----------|
| `nav-md` | **768px** | Mobile vs tablet overlay styling (both are overlay until 1024) |
| `nav-lg` | **1024px** | Persistent rail vs overlay; hamburger visibility; main `margin-left` |

---

## 2. App bar

Shared tokens: height **64px**, background `--bg-base`, border-bottom `--border-subtle`, sticky `z-index: 40`.

### Compact app bar (`< 1024px`) — mobile + tablet

| Element | Spec |
|---------|------|
| Hamburger | Material `menu`, `--primary`; visible **< 1024px** |
| Brand | **Platform Admin**, Inter **700**, `--primary` |
| Logout | Inter 14px, `--text-secondary`, hover `--accent-hover` |

### Desktop app bar (`≥ 1024px`) — as-built

| Element | Spec |
|---------|------|
| Logo badge | **32×32px**, `--primary-container` bg, `terminal` icon (filled), `--on-primary-container` |
| Brand | **Platform Admin**, Inter **700**, `--primary` |
| User cluster | Right-aligned; border-left `--border-subtle`; display name from JWT email (Inter 14px) + “Superuser” (JetBrains Mono 12px); Logout button |
| Hamburger | **Hidden** |

### Deferred (not in current Client build)

| Element | Planned spec |
|---------|----------------|
| Search chip | `--surface-container-low`, **Cmd + K** label; global search |
| Notifications | `notifications` icon |
| Account menu | `account_circle` icon |

---

## 3. Desktop mini-drawer (`≥ 1024px`)

### Structure

```
fixed left-0 top-0 h-full pt-16 z-30
width: 64px → 280px on :hover / :focus-within
overflow: hidden
background: --surface-container-low
border-right: 1px --border-subtle
```

Main content: **`margin-left: 64px` always** — hover expansion is an **overlay**, not a layout push.

### Transitions

| Property | Value |
|----------|-------|
| Duration | **300ms** |
| Easing | `cubic-bezier(0.4, 0, 0.2, 1)` |
| Properties | `width`, `opacity`, `box-shadow` |

### Nav item (desktop)

| Token | Value |
|-------|-------|
| Height | **48px** (`h-12`) |
| Padding | none (full-width rows) |
| Border radius | none |
| Icon column | **64px** fixed, centered |
| Label | JetBrains Mono **13px**; hidden until sidebar expand |
| Gap between items | **4px** |

**Inactive:** `--text-secondary`; hover → `--on-surface` + `--surface-container`.

**Active:**

| Property | Token |
|----------|-------|
| Text / icon | `--primary` |
| Left border | **2px** solid `--accent` |
| Background | `--surface-container-highest` |
| Label weight | **600** |

### Desktop footer — Admin profile

| Token | Value |
|-------|-------|
| Layout | `border-top` `--border-subtle`; height **64px** |
| Avatar | **40px** circle, `--surface-container-highest` bg, `person` icon `--primary` |
| Name | JetBrains Mono **13px** bold, `--text-primary` |
| Role | JetBrains Mono **10px** uppercase tracking, `--text-secondary` |
| Label reveal | Name + role hidden until sidebar `:hover` or `:focus-within` |

User identity also appears in the **desktop app bar** user cluster (Stitch-aligned duplicate).

### Label reveal (desktop)

| Element | Trigger | Behavior |
|---------|---------|----------|
| Nav labels | Sidebar `:hover` or `:focus-within` | `opacity: 0 → 1` |
| Profile meta | Sidebar `:hover` or `:focus-within` | `opacity: 0 → 1` |

Collapsed rail shows **icons only**. Each `NavLink` includes a **`title`** attribute.

### Hover expand

- Width **64px → 280px** (`--nav-rail-expanded-width`)
- `box-shadow: 4px 0 24px rgba(0, 0, 0, 0.35)`
- `z-index: 35`

---

## 4. Tablet overlay (`768px – 1023px`)

### Scrim

- `background: rgba(0, 0, 0, 0.6)`
- `backdrop-filter: blur(4px)`
- Closes drawer on overlay click

### Drawer panel

| Token | Value |
|-------|------|
| Anchor | **Left** |
| Width | **280px** (`17.5rem`) |
| Background | `--surface-container-low` |
| Header | **Admin Menu** title (Inter 20px bold `--primary`); bottom border; **no close button** |
| Nav items | `px-4`, gap **16px**, height **48px** |
| Labels | Always visible; JetBrains Mono **13px** |
| Active item | **2px** left border `--accent`, `--primary` text, `--surface-container-highest` bg |
| Footer | **Admin profile** — avatar + Admin / Superuser |
| Close on navigate | Yes |
| Escape | Yes |

---

## 5. Mobile overlay (`< 768px`)

### Scrim

- `background: rgba(16, 21, 12, 0.9)` (≈ `--background` at 90%)
- `backdrop-filter: blur(4px)`

### Drawer panel

| Token | Value |
|-------|------|
| Anchor | **Left** |
| Width | **280px**, max **85vw** |
| Background | `--surface-container-low` |
| Header | **Admin Menu** title + **close** (`×`) button |
| Nav items | Height **48px**, `px-4`, gap **16px** |
| Labels | Always visible; Inter **14px** |
| Active item | **2px** left border `--accent`, `--primary` text, `--surface-container-highest` bg |
| Footer | **Admin profile** — avatar + Admin / Superuser; Logout in **app bar** only |
| Close on navigate | Yes |
| Escape | Yes |

---

## 6. Close behavior matrix

| Action | Mobile | Tablet | Desktop |
|--------|--------|--------|---------|
| Hamburger toggle | Open/close | Open/close | N/A (hidden) |
| Close button | Yes | No | N/A |
| Overlay click | Close | Close | N/A |
| Escape | Close | Close | N/A |
| Navigate link | Close | Close | N/A (rail stays) |

---

## 7. Navigation IA

| Route | Label | Material icon |
|-------|-------|---------------|
| `/` | Dashboard | `dashboard` |
| `/customers` | Customers | `group` |
| `/services` | Service Catalog | `inventory_2` |
| `/licenses` | Licenses | `vpn_key` |
| `/invoices` | Invoices | `receipt_long` |
| `/integration-keys` | Integration Keys | `key` |
| `/audit` | Audit Log | `history` |
| `/validate` | Validate License | `verified_user` |
| `/tools/validate` | *(alias → `/validate`)* | Redirect or same component |
| `/login` | — | No shell / no drawer |

---

## 8. MudBlazor implementation map

| Reference | Blazor |
|-----------|--------|
| Fixed `<aside>` | `MudDrawer` `Variant="Mini"`, `Breakpoint="Lg"`, `OpenMiniOnHover="false"` |
| `nav-lg` rail | CSS `@media (min-width: 1024px)` on `.platform-sidebar` |
| Overlay tiers | Mud temporary drawer below `Lg`; `@bind-Open` toggles overlay |
| Hamburger visibility | CSS hide `.overlay-menu-btn` at `min-width: 1024px` |
| App bar variants | `.app-bar-compact` / `.app-bar-desktop` in `MainLayout.razor` |
| Custom nav markup | `NavMenu.razor` with shared profile footer |
| Overlay header | `.drawer-header-overlay` — Admin Menu + close (mobile only) |
| Clip below app bar | `ClipMode="DrawerClipMode.Always"` |
| Desktop rail expand | CSS `aside.platform-sidebar-rail` **64px → 280px** on hover (not MudDrawer) |
| Viewport sync | `platformShell.syncNavMode()` — auto-open drawer on desktop, close on overlay |

**Do not** use `OpenMiniOnHover` — hover expand is **CSS-only** with fixed `margin-left: 64px` on `MudMainContent`.

### Key files

- `Client/Layout/MainLayout.razor` / `.razor.css`
- `Client/Layout/NavMenu.razor` / `.razor.css`
- `Client/wwwroot/index.html` — `platformShell` helpers

### platformShell JS API

```js
platformShell.isNavOverlay()   // max-width 1023.98px
platformShell.isMobileNav()    // max-width 767.98px
platformShell.isTabletNav()    // 768–1023px
platformShell.isDesktopNav()   // min-width 1024px
platformShell.syncNavMode()    // body.nav-desktop / nav-overlay
platformShell.registerNavShell(dotNetRef)  // Escape closes overlay drawer
```

---

## 9. Accessibility

See [design-system.md](design-system.md#accessibility) for global focus, contrast, and motion rules.

- Nav: `<nav aria-label="Main navigation">`
- Icon-only collapsed state: **`title`** on each `NavLink`
- Active route: `aria-current="page"` (Blazor `NavLink` default)
- Focus: `2px solid --accent`, inset −2px offset on nav items
- Hamburger / close: `aria-label` + `aria-expanded` on menu button
- Minimum touch target **48px** on overlay nav rows (mobile + tablet)
- Sidebar expand: **`:hover` and `:focus-within`** — never hover-only
- Escape closes overlay tiers (mobile + tablet)
- Skip link to `#main-content` on `MudMainContent`

---

## 10. Do not

- Push main content when sidebar expands on desktop
- Use MudBlazor default Bootstrap navbar
- Show plain labels in 64px collapsed rail (icons only)
- Use `OpenMiniOnHover` with layout-shifting drawer open state
- Anchor overlay drawers to the **right** (always **left**)
- Show hamburger on desktop (`≥ 1024px`)
