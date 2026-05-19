# SaaS License Admin Hub — UI/UX Specification

**Version:** 1.0 (aligned with Qwen design session, May 2026)  
**Stack:** Blazor WebAssembly + MudBlazor v7+  
**Theme:** Dark-first, inspired by [helmut-essien.github.io/portfolio](https://helmut-essien.github.io/portfolio/)

Cursor skills: `.cursor/skills/platform-admin-ui/` (agent) · Backend: `.cursor/skills/platform-license-hub/`

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
| Accessibility | WCAG 2.1 AA |
| Performance | Virtualized grids, skeletons |
| Viewports | Desktop-first; tablet ≥768px |

---

## 3. Color palette (exact)

| Token | Hex | Usage |
|-------|-----|--------|
| primary | `#71e215` | Buttons, active nav, success, Active license |
| primary-hover | `#5c9f24` | Hover, nav underline |
| accent | `#FFCC00` | Focus ring, warnings, CTA hover border |
| background | `#000000` | Page |
| surface | `#0c1408` | Sidebar, cards, modals |
| surface-elevated | `#1a1a1a` | Dropdowns, row hover |
| text-primary | `#ffffff` | Headings |
| text-secondary | `rgba(255,255,255,0.7)` | Nav |
| text-body | `#FAF5E9` | Body, cells |
| success | `#71e215` | Active |
| warning | `#FFCC00` | Expiring |
| suspended | `#f59e0b` | Suspended |
| error | `#ef4444` | Revoked, destructive |
| info | `#60a5fa` | Info |
| border | `#333333` | Inputs, tables |
| card-glow | `0 0 20px rgba(113,226,21,0.3)` | Hover |

---

## 4. Typography

| Role | Font |
|------|------|
| UI | **Inter** (400, 500, 600, 700) |
| Code / keys / JSON | **JetBrains Mono** (400, 500) |

Minimum body: **14px**. Metric values: **32px**, weight **700**.

Load fonts in `index.html` (see `.cursor/skills/platform-admin-ui/design-system.md`).

---

## 5. Navigation

| Route | Label |
|-------|-------|
| `/` | Dashboard |
| `/customers` | Customers |
| `/services` | Service Catalog |
| `/licenses` | Licenses |
| `/audit` | Audit Log |
| `/tools/validate` | Validate License |
| `/login` | Login (no drawer) |
| `/services/{id}/keys` | Integration Keys |

Active: `#71e215` + underline `#5c9f24`. Inactive: `rgba(255,255,255,0.7)`.

---

## 6. Screens summary

| # | Route | Summary |
|---|-------|---------|
| 1 | `/` | KPI cards, audit timeline, quick actions |
| 2 | `/customers` | Grid, filters, drawer, suspend/delete confirm |
| 3 | `/services` | Catalog CRUD, availability toggle |
| 4 | `/licenses` | Global grid, issue modal, bulk actions |
| 5 | `/customers/{id}/licenses` | Filtered license list |
| 6 | `/audit` | Expandable JSON, CSV export |
| 7 | `/services/{id}/keys` | Regenerate key once |
| 8 | `/tools/validate` | Test validation API |
| — | `/login` | JWT login |

Detailed specs: `.cursor/skills/platform-admin-ui/screens.md`

---

## 7. UX requirements

- Destructive actions → confirmation modal (`#ef4444`)
- Status-first badges on all licenses/customers
- Progressive disclosure (grid → drawer/expand)
- Copy buttons on keys with success glow
- Empty states with CTA on every grid
- Skeleton loaders; no blank waits
- Inline validation + Snackbar for API errors
- Keyboard: Tab, Escape, Enter; focus `#FFCC00`
- Dark surfaces: `#000` → `#0c1408` → `#1a1a1a`

---

## 8. Phase 1 wireframe (shell + dashboard)

### Shell

- App bar `#000000`
- Drawer `#0c1408`, 240px
- Content `#000000`, padding 24px

### Dashboard grid

- Row 1: 4 metric cards (Customers, Active Licenses, Expiring 7d, Suspended)
- Row 2: Quick actions (New Customer, Issue License, Generate Integration Key)
- Row 3: Timeline — last 10 audit events

Responsive: 4 cols → 2 cols → 1 col.

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

- `#71e215` on `#000000` — AAA (~7.2:1)
- `#FAF5E9` on `#0c1408` — AAA
- Focus: 2px `#FFCC00` outline, 2px offset
- Icon buttons: `aria-label`
- Status not color-only (text + chip label)

---

## 12. Color & typography quick map

```
Buttons/CTA     → #71e215 bg, #000 text, hover border #FFCC00
Page bg         → #000000
Cards/sidebar   → #0c1408
Body copy       → #FAF5E9
Headings        → #ffffff
Keys/JSON       → JetBrains Mono, #71e215
Focus           → #FFCC00
Destroy         → #ef4444
```

---

*Generated for the Platform repository. Implement Blazor components per phase; this document is the design source of truth.*
