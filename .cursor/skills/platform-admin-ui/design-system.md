---
title: "Platform Admin UI Design System (Minimalist)"
version: "1.3.0"
type: "design-system"
description: "Dark admin console — lime primary + accent KPI values, Material-inspired surfaces"
author: "Helmut Essien"
last_updated: "2026-06-01"
---

# Design system — Platform Admin UI

Based on [helmut-essien.github.io](https://helmut-essien.github.io/portfolio/) and the dashboard reference mock (May 2026).

**Viewport strategy:** Mobile-ready, **desktop-optimized** admin console (dense grids and multi-column layouts target ≥960px).

---

## CSS custom properties

```css
:root {
  /* Brand */
  --primary: #92d959;          /* headings, active nav text, primary CTA fill */
  --on-primary: #193800;       /* text on primary buttons */
  --accent: #5c9f24;           /* KPI values, keys, focus ring, accent CTAs */
  --accent-hover: #7ccf2e;
  --accent-active: #3a7014;

  /* Surfaces */
  --bg-base: #10150c;          /* page canvas */
  --bg-surface: #1e1e1e;      /* cards, panels */
  --bg-elevated: #2a2a2a;
  --surface-container-low: #191d14;   /* drawer */
  --surface-container: #1d2118;
  --surface-container-high: #272b22;  /* row / button hover */
  --surface-container-highest: #32362c; /* active nav item */

  /* Text */
  --text-primary: #ededed;
  --text-secondary: #a0a0a0;
  --on-surface: #e0e4d6;
  --text-error: #ffb4ab;       /* attention timeline dots, alerts only */

  /* Structure */
  --border-subtle: #2c2c2c;

  /* Layout spacing */
  --container-margin: 24px;
  --section-gap: 32px;
  --gutter: 16px;
}
```

---

## Breakpoint cheat sheet

Three systems coexist — **never mix them in the same CSS rule without commenting which system you mean**.

| System | Token | Pixel width | Used for |
|--------|-------|-------------|----------|
| **Navigation shell** | `nav-lg` | **1024px** | Persistent rail vs overlay; hamburger visibility; main `margin-left`. See [navigation-shell.md](navigation-shell.md). |
| **Navigation shell** | `nav-md` | **768px** | Mobile vs tablet overlay styling (both overlay until 1024). |
| **Page layout** | `xs` | <600px | Mobile base, card lists, full-width buttons |
| **Page layout** | `sm` | ≥600px | 2-column grids, wider dialogs |
| **Page layout** | `md` | **≥960px** | `MudDataGrid`, inline filters, 2rem page padding |
| **Page layout** | `lg` | ≥1280px | 4-column KPIs, 48px page padding |
| **Page layout** | `xl` | ≥1920px | Centered max-width content |
| **MudBlazor enum** | `Breakpoint.Lg` | 1280px | Drawer temporary vs mini (implementation) |
| **MudBlazor enum** | `Breakpoint.Md` | 960px | MudGrid / `MudHidden` for page components |

**Implementation rule:** Shell sidebar CSS uses **`nav-lg` = 1024px** and **`nav-md` = 768px**. Page CSS uses **`960px`** for `md+`. MudDrawer uses **`Breakpoint.Lg`** with CSS overrides at 1024px for persistent rail.

---

## Typography

| Role | Font | Size | Weight |
|------|------|------|--------|
| Page title (H1) | Inter | 32px / 2rem | 600 | Color **`--primary`** via `.page-title` |
| Section title (H2) | Inter | 20px | 700 |
| Body | Inter | 14px | 400 |
| KPI value | Inter | 32px | 700 |
| Nav labels (desktop expanded) | **JetBrains Mono** | 13px | 400–600 |
| Nav labels (mobile overlay) | Inter | 14px | 400 |
| Labels / captions / mono UI | JetBrains Mono | 11–13px | 400–700 |
| Keys / JSON | JetBrains Mono | 12–13px | 400 |

Load in `Client/wwwroot/index.html`:

```html
<link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&family=JetBrains+Mono:wght@400;500&display=swap" rel="stylesheet" />
```

(Weights match [`Client/wwwroot/index.html`](../../../Client/wwwroot/index.html).)

---

## Page layout primitives (`Client/wwwroot/css/app.css`)

Implemented patterns — full detail in [implementation-patterns.md](implementation-patterns.md).

| Class | Usage |
|-------|--------|
| `.page-title` | All page H1s — `color: var(--primary)` |
| `.page-subtitle` | Muted subtitle under H1 |
| `.page-content` | Centered max-width column on every `*-page` root |
| `.page-title-drawer` | Customer name in detail drawer |
| `.demo-badge` | “Demo data” on mock KPIs / charts |

---

## WASM boot splash

Before Blazor starts, `#app` shows branded splash (not the default SVG ring):

- Logo: `images/login-logo.png`, `border-radius: 1.25rem`
- Progress: 3px bar, `--blazor-load-percentage` width, accent→primary gradient + shimmer
- Background: `--bg-base`; `theme-color` `#10150c`

See [implementation-patterns.md](implementation-patterns.md#wasm-boot-splash).

---

## Dialogs (MudBlazor)

| Layer | Spec |
|-------|------|
| Provider | `<MudDialogProvider MaxWidth="MaxWidth.Medium" FullWidth="true" />` in `App.razor` |
| Form dialogs | Inherit provider default (~960px) |
| Confirm | `PlatformDialogOptions.Confirm` — `MaxWidth.Small`, `FullWidth` true |
| Key reveal | `PlatformDialogOptions.KeyReveal` — Small, no escape/backdrop dismiss |

---

## CTA hierarchy

| Type | Fill | Text | Usage |
|------|------|------|--------|
| **Primary CTA** | `--primary` | `--on-primary` | Create/save actions in app shell pages (+ New Customer, CREATE INVOICE) |
| **Accent CTA** | `--accent` | `--bg-base` | Login submit, **destructive confirm**, snackbar success background |
| **Secondary CTA** | transparent + `--border-subtle` border | `--text-primary` | Cancel, export, secondary toolbar actions |

Do not use `--primary` and `--accent` fills interchangeably on the same screen without hierarchy reason.

---

## MudTheme (`Client/Theme/PlatformTheme.cs`)

| Mud token | Hex | Usage |
|-----------|-----|--------|
| Primary | `#92d959` | Filled primary buttons, active nav icons |
| Secondary | `#5c9f24` | Secondary accent |
| Background | `#10150c` | Page |
| Surface | `#1e1e1e` | Cards |
| DrawerBackground | `#191d14` | Sidebar |
| TextPrimary | `#ededed` | Body |
| TextSecondary | `#a0a0a0` | Captions |
| Error / Warning | `#ffb4ab` | Attention states only |

Default border radius: **2px** (sharp admin aesthetic).

---

## Shell

Sidebar-only detail: [navigation-shell.md](navigation-shell.md).

| Element | Spec |
|---------|------|
| App bar (compact) | 64px; `< 1024px`; hamburger + **Platform Admin** + logout |
| App bar (desktop) | 64px; `≥ 1024px`; terminal logo badge, **Platform Admin**, user display name + “Superuser”, logout |
| Hamburger | `--primary` menu icon; visible **only < 1024px** |
| Drawer (≥ 1024px) | Fixed **64px** rail, hover/focus-within expand to **280px** overlay; `--surface-container-low`; **profile** footer; **no main-content push** |
| Drawer (768–1023px) | Left overlay **280px**; **Admin Menu** header; **profile** footer; scrim `rgba(0,0,0,0.6)` + blur |
| Drawer (< 768px) | Left overlay **280px** (max 85vw); **Admin Menu** + close; **profile** footer; logout in app bar; scrim `rgba(16,21,12,0.9)` + blur |
| Deferred chrome | Cmd+K search, notifications, account menu — spec in navigation-shell; **not in current Client build** |
| Active nav | `--primary` text/icon, **2px** left border **`--accent`**, bg `--surface-container-highest` |
| Inactive nav | `--text-secondary`, hover `--on-surface` + `--surface-container` |
| Nav item height | **40px** desktop; **48px** tablet; **56px** mobile overlay |
| Nav labels | JetBrains Mono **13px** (desktop expanded / tablet); Inter **14px** (mobile overlay); hidden in collapsed rail until sidebar hover/focus |
| Main content | `--bg-base`, **`margin-left: 64px`** at ≥ 1024px; padding `--container-margin` (24px) → 48px lg+ |
| Transitions | **300ms** `cubic-bezier(0.4, 0, 0.2, 1)` on width and opacity (respect reduced motion) |

---

## Component tokens

| Pattern | Spec |
|---------|------|
| KPI card | `--bg-surface`, 1px `--border-subtle`, padding 24px; hover border `--primary` |
| KPI label | JetBrains Mono uppercase 11px `--text-secondary` |
| KPI value | 32px bold `--accent` |
| Primary CTA | `--primary` bg, `--on-primary` text, bold |
| Secondary CTA | 1px `--border-subtle`, hover `--surface-container-high` |
| Timeline dot (latest) | filled `--primary` |
| Timeline dot (attention) | `--text-error` |
| Timeline dot (default) | `--border-subtle` ring |
| Card hover | border shifts to `--primary` at ~35% opacity |
| **Bulk actions bar** | Fixed bottom, glass (`backdrop-filter: blur(12px)`), `--bg-elevated` at 90% opacity, 1px `--border-subtle`, padding 12px 24px; used on Licenses multi-select |
| **Customer status badge** | `.status-badge-active` (lime tint) / `.status-badge-suspended` (gray) — grid + drawer |
| **Filter select “all”** | MudSelect string filters use value `"all"` for unfiltered state — never `""` |

---

## Feedback states

Canonical patterns for loading, empty, error, and toast feedback.

### Loading

- **Page / section:** `MudSkeleton` rectangles on `--bg-surface`; never blank white/dark voids.
- **Submit button:** `_busy = true` → `MudProgressCircular` Size.Small + “Processing…” / “Signing in…”; disable all inputs.
- **Grid refresh:** skeleton rows or inline progress on filter bar; keep header visible.

### Empty

- Centered message + optional icon in `--text-secondary`.
- **Primary CTA** below copy (e.g. “+ New Customer”) using `--primary` fill.
- Minimum one action — never dead-end grids.

### Error

| Context | Pattern |
|---------|---------|
| Form field | MudBlazor inline via `For` + annotations; color `--accent` |
| Form / page API | `MudAlert` Severity.Error, dismissible, server `message` at top |
| Transient global | `ISnackbar` Severity.Error (see Snackbar below) |

### Snackbar (canonical)

**Override MudBlazor default green** for brand consistency:

| Severity | Background | Text | When |
|----------|------------|------|------|
| Success | `--accent` (`#5c9f24`) | `--bg-base` (`#10150c`) | Save, copy, revoke complete |
| Error | Mud default or `--text-error` on dark surface | `--text-primary` | API failure, network error |
| Info | `--bg-elevated` | `--text-primary` | Neutral notices |

```csharp
Snackbar.Add("Customer saved.", Severity.Success); // Theme/snackbar config applies accent styling
```

Configure in `PlatformTheme` or `app.css` MudSnackbar overrides — **do not** rely on MudBlazor’s stock green success.

---

## Destructive confirmation dialog

Required for revoke, suspend, delete, and key rotation.

| Element | Spec |
|---------|------|
| Component | `MudDialog` or `DialogService.ShowMessageBox` |
| Title | Verb + entity: “Revoke license?” |
| Body | One sentence on consequence (irreversible, customer impact) |
| Cancel | `Variant.Text`, `--text-secondary`; **default focus** on open |
| Confirm | `Variant.Filled`, **`--accent`** bg, `--bg-base` text; label matches verb (“Revoke”, “Suspend”) |
| Keyboard | Escape → cancel; Enter → **does not** confirm unless focus on Confirm (avoid accidental confirm) |
| Overlay | Standard MudDialog scrim |

---

## Accessibility

Target: **WCAG 2.1 AA** where feasible for an internal admin tool.

### Contrast (documented pairs)

| Foreground | Background | Usage | AA |
|------------|------------|-------|-----|
| `#92d959` | `#10150c` | Headings, nav active, brand | Large text ✓; verify small bold |
| `#ededed` | `#10150c` | Body on canvas | ✓ |
| `#ededed` | `#1e1e1e` | Body on cards | ✓ |
| `#a0a0a0` | `#1e1e1e` | Secondary labels | ✓ (~4.5:1+) |
| `#5c9f24` | `#1e1e1e` | KPI values, keys | ✓ |
| `#193800` | `#92d959` | Text on primary buttons | ✓ |
| `#ffb4ab` | `#10150c` | Error / attention dots | Use sparingly; pair with text label |

Re-verify when changing tokens. Status must never rely on color alone — always include text (`Active`, `Revoked`, etc.).

### Focus rings

| Context | Rule |
|---------|------|
| **Default** (buttons, inputs, links) | `2px solid --accent`, **+2px offset** (`app.css` `*:focus-visible`) |
| **Nav items** (inside drawer) | `2px solid --accent`, **−2px inset offset** (avoids clipping in 64px rail) |
| **Dialogs** | Trap focus; restore on close |

### Motion (`prefers-reduced-motion`)

When `@media (prefers-reduced-motion: reduce)`:

- Disable sidebar width/opacity transitions (instant expand/collapse).
- Disable KPI card hover translate and login slide-up animation.
- Keep opacity fades ≤ 0ms or remove.

Login page already disables slide-up — **apply the same rule globally** in `app.css`.

### Keyboard & screen readers

- Tab order follows visual DOM order.
- Escape closes dialogs and mobile nav overlay.
- Enter submits primary form action when focus is in a field (not globally on destructive dialogs).
- Icon-only buttons: **`aria-label`** required.
- KPI cards: `role="link"` + descriptive `aria-label`.
- Optional: skip link `<a href="#main-content" class="skip-link">Skip to content</a>` — visually hidden until focused.

### Hover-only UI

Collapsed sidebar labels require hover to expand — **must also expand on `:focus-within`** for keyboard users. See [navigation-shell.md](navigation-shell.md). Every icon-only nav link keeps a `title` attribute as minimum fallback.

---

## Do not

- Reintroduce multi-color semantic palettes (success/warning/info blues)
- Use rounded “pill” buttons on dashboard quick actions (reference uses rectangular admin buttons)
- Show plain license keys outside one-time dialogs
- Rely on MudBlazor default green snackbar for success toasts
- Use hover as the only way to reveal critical navigation labels (pair with focus-within)
