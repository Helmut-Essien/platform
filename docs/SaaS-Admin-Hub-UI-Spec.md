# SaaS License Admin Hub — UI/UX Specification

**Version:** 1.3 (three-tier navigation shell, May 2026)  
**Stack:** Blazor WebAssembly + MudBlazor v7+  
**Theme:** Dark-first, inspired by [helmut-essien.github.io/portfolio](https://helmut-essien.github.io/portfolio/)

Cursor skills: `.cursor/skills/platform-admin-ui/` (agent) · Backend: `.cursor/skills/platform-license-hub/`

**Viewport strategy:** Mobile-ready, **desktop-optimized** admin console.

---

## 1. Design goal

Production-ready admin interface for the developer/owner to:

1. Register and manage **customer organizations**
2. Define **software services** in a catalog (Hostel, Laundry, School, Asset)
3. **Issue, suspend, renew, revoke** licenses per customer per service
4. View **license key delivery status** (generated, hashed, emailed)
5. Browse a filterable **audit trail**
6. Manage **integration keys** per service product
7. **Test license validation** without leaving the hub

---

## 2. Constraints

| Area | Choice |
|------|--------|
| Framework | Blazor WASM + MudBlazor |
| State | MudBlazor + HttpClient only |
| Accessibility | WCAG 2.1 AA (see §11 and design-system accessibility) |
| Performance | Virtualized grids, skeletons |
| Viewports | Desktop-optimized; mobile-ready; nav tiers at **768px** / **1024px**; content grids at **960px** |

---

## 3. Color palette (exact)

| Token | Hex | Usage |
|-------|-----|--------|
| primary | `#92d959` | Headings, active nav, primary CTA fill |
| on-primary | `#193800` | Text on primary buttons |
| accent | `#5c9f24` | KPI values, keys, focus ring |
| accent-hover | `#7ccf2e` | Hover states |
| accent-active | `#3a7014` | Active press |
| background | `#10150c` | Page canvas |
| bg-surface | `#1e1e1e` | Cards, panels |
| surface-container-low | `#191d14` | Drawer |
| surface-container | `#1d2118` | Hover fills |
| surface-container-high | `#272b22` | Secondary button hover |
| surface-container-highest | `#32362c` | Active nav item |
| text-primary | `#ededed` | Body |
| text-secondary | `#a0a0a0` | Captions, labels |
| on-surface | `#e0e4d6` | Nav hover text |
| text-error | `#ffb4ab` | Attention timeline dots only |
| border-subtle | `#2c2c2c` | Cards, inputs, dividers |
| card-hover | border `#92d959` | KPI / panel hover |

---

## 4. Typography

| Role | Font |
|------|------|
| UI | **Inter** (400, 600, 700) |
| Labels / KPI captions / nav | **JetBrains Mono** (400, 700) |
| Code / keys / JSON | **JetBrains Mono** (400, 500) |

Minimum body: **14px**. KPI values: **32px**, weight **700**, color `#5c9f24`.

Load fonts in `index.html` (see `.cursor/skills/platform-admin-ui/design-system.md`).

---

## 5. Navigation

Full responsive shell spec: `.cursor/skills/platform-admin-ui/navigation-shell.md`

| Route | Label |
|-------|-------|
| `/` | Dashboard |
| `/customers` | Customers |
| `/services` | Service Catalog |
| `/licenses` | Licenses |
| `/invoices` | Invoices (list, detail, record payment) |
| `/integration-keys` | Integration Keys |
| `/audit` | Audit Log |
| `/validate` | Validate License |
| `/tools/validate` | Alias → `/validate` |
| `/login` | Login (no drawer) |

### Sidebar behavior

| Viewport | Behavior |
|----------|----------|
| **≥ 1024px** (desktop) | Persistent **64px** icon rail; hover/focus-within → **240px** overlay; **Settings** footer; rich app bar (terminal logo, Cmd+K, notifications) |
| **768px – 1023px** (tablet) | Hamburger → **left** overlay **280px**; profile header; **System Status** footer; scrim `rgba(0,0,0,0.6)` |
| **< 768px** (mobile) | Hamburger → **left** overlay **280px** (max 85vw); profile header + close; **Logout** footer; scrim `rgba(16,21,12,0.9)` |

**Active:** `#92d959` text/icon, **2px `#5c9f24` left border**, `#32362c` background (all tiers).  
**Inactive:** `#a0a0a0`; hover `#e0e4d6` on `#1d2118`.  
**Labels:** JetBrains Mono 13px (desktop expanded / tablet); Inter 14px (mobile overlay); hidden in collapsed rail until sidebar **hover or focus-within**.

Breakpoint cheat sheet: `.cursor/skills/platform-admin-ui/design-system.md#breakpoint-cheat-sheet`

---

## 6. Screens summary

| # | Route | Summary |
|---|-------|---------|
| 1 | `/` | KPI grid, custom audit timeline, sidebar quick actions + platform health |
| 2 | `/customers` | Filter bar, avatar grid, MoreVert menu, detail drawer with tabs |
| 3 | `/services` | Catalog CRUD, availability toggle |
| 4 | `/licenses` | Global grid, issue modal, bulk actions |
| 5 | `/customers/{id}/licenses` | Filtered license list (`?customerId=`) |
| 6 | `/audit` | Expandable JSON, CSV export |
| 7 | `/integration-keys` | Regenerate key once |
| 8 | `/tools/validate` | Test validation API |
| — | `/login` | JWT login |

Detailed specs: `.cursor/skills/platform-admin-ui/screens.md`

---

## 7. UX requirements

- Destructive actions → [destructive confirmation dialog](.cursor/skills/platform-admin-ui/design-system.md#destructive-confirmation-dialog) (accent confirm, cancel default focus)
- Status-first badges on all licenses/customers
- Progressive disclosure (grid → drawer/expand)
- Copy buttons on keys with success glow
- [Empty states](.cursor/skills/platform-admin-ui/design-system.md#empty) with CTA on every grid
- [Skeleton loaders](.cursor/skills/platform-admin-ui/design-system.md#loading); no blank waits
- Inline validation + Snackbar/`MudAlert` for API errors
- [Snackbar success](.cursor/skills/platform-admin-ui/design-system.md#snackbar-canonical): `--accent` bg, not Mud default green
- Keyboard: Tab, Escape, Enter; focus per design-system
- [`prefers-reduced-motion`](.cursor/skills/platform-admin-ui/design-system.md#motion-prefers-reduced-motion) globally
- Dark surfaces: `#10150c` → `#1e1e1e` → `#272b22`

---

## 8. Phase 1 wireframe (shell + dashboard)

### Shell

Detail: `.cursor/skills/platform-admin-ui/navigation-shell.md`

- App bar `#10150c`, **64px**, sticky, title in `#92d959`
- **≥ 1024px:** drawer `#191d14`, **64px** rail → **240px** hover overlay; Settings footer; rich app bar; main **`margin-left: 64px`**
- **768–1023px:** hamburger, **left 280px** drawer, System Status footer, 60% scrim
- **< 768px:** hamburger, **left 280px** drawer (max 85vw), Logout footer, close button, 90% scrim
- Content `#10150c`, padding 24px (48px lg+)

### Dashboard grid

- **Row 1:** 4 KPI cards (Customers, Active Licenses, Expiring 30d, Unpaid Invoices) — mono labels, accent values, contextual footnotes
- **Row 2:** 2:1 asymmetric layout
  - **Left:** Recent Activity custom timeline (10 events) + “View Full Audit Log”
  - **Right:** Quick Actions stack (+ New Customer primary, Issue License, Generate Integration Key) + Platform Health bars

Responsive: KPI 4 → 2 → 1 cols; main grid stacks on mobile.

### Service Catalog

Reference mock (May 2026): success banner, 4 stat cards, styled service table with inline availability switches, integration key chips, insights row (performance matrix + health checks).

### Licenses

Reference mock (May 2026): 4-column filter grid, multi-select license table with status pills and Key Sent chips, fixed glass bulk actions bar (Resend Keys, Renew, Revoke).

### Invoices

Reference mock (May 2026): primary H1, CREATE INVOICE + EXPORT CSV CTAs, 4 stat cards with quick filter chips, filter panel, styled invoice grid with customer avatars and status pills, insights row (billing rules + Q3 projection).

### Integration Keys

Reference mock (May 2026): security notice banner, 3 stat cards, bento grid of service key cards with masked keys, active pills, and revoke actions; Generate New Key dialog + one-time reveal.

### Audit Log

Reference mock (May 2026): primary H1 with header search + Export CSV, expandable audit table with admin avatars, action badges, JSON detail panels, client pagination footer, and 4 stat cards.

### Validate License

Reference mock (May 2026): “License Debugger” split layout — input parameters panel (license key, service context, integration key) + terminal-style JSON response with copy/clear, status/latency bar.

Full ASCII wireframes: `.cursor/skills/platform-admin-ui/wireframes-phase1.md`

---

## 9. MudTheme

Complete C# `PlatformTheme.Dark` snippet: `.cursor/skills/platform-admin-ui/design-system.md`

---

## 10. UI delivery phases

| Phase | Deliverable |
|-------|-------------|
| UI-1 | Shell + Dashboard |
| UI-2 | Customers + Services |
| UI-3 | Licenses |
| UI-4 | Audit + Integration keys |
| UI-5 | Validate tool + Login |

Confirm before each phase. Backend API phases (2–6) run separately per `platform-license-hub`.

---

## 11. Accessibility checklist

Full spec: `.cursor/skills/platform-admin-ui/design-system.md#accessibility`

### Contrast

- `#92d959` on `#10150c` — headings/nav (large text AA)
- `#ededed` on `#10150c` / `#1e1e1e` — body AA
- `#a0a0a0` on `#1e1e1e` — secondary labels AA
- `#5c9f24` on `#1e1e1e` — KPI values AA
- `#193800` on `#92d959` — primary button text AA

### Interaction

- Focus: `2px #5c9f24` — **+2px offset** default; **−2px inset** on nav items
- Icon buttons: `aria-label`
- Status not color-only (text + chip label)
- KPI cards: `role="link"` + descriptive `aria-label`
- Sidebar: expand on **hover and focus-within**; `title` on collapsed nav links
- Optional skip link to `#main-content`
- `prefers-reduced-motion`: disable sidebar/KPI/login transitions

---

## 12. Color & typography quick map

```
Headings/nav   → #92d959
KPI values     → #5c9f24
Page bg        → #10150c
Cards/sidebar  → #1e1e1e / #191d14
Body copy      → #ededed
Labels/mono    → JetBrains Mono 11–13px
Primary CTA    → #92d959 bg, #193800 text
Focus          → #5c9f24
Attention dot  → #ffb4ab
```

---

*Generated for the Platform repository. Implement Blazor components per phase; skill docs in `.cursor/skills/platform-admin-ui/` are the implementation source of truth.*
