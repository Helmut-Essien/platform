---
title: "Platform Admin UI Design System (Minimalist)"
version: "1.0.0-minimal"
type: "design-system"
description: "Dark theme with single lime green accent – no extra colors, no noise"
author: "Helmut Essien"
---

# Design system — Platform Admin UI (Minimalist)

Based on [helmut-essien.github.io](https://helmut-essien.github.io/portfolio/) but reduced to essential minimalism.

## CSS custom properties

```css
:root {
  /* Single accent – your requested #5c9f24 */
  --accent: #5c9f24;
  --accent-hover: #7ccf2e;
  --accent-active: #3a7014;

  /* Neutrals – completely desaturated grays */
  --bg-base: #121212;
  --bg-surface: #1e1e1e;
  --bg-elevated: #2a2a2a;     /* only if needed for modals/dialogs */
  
  /* Text – neutral off-white and one muted gray */
  --text-primary: #ededed;
  --text-secondary: #a0a0a0;
  
  /* Borders – only when required for structure */
  --border-subtle: #2c2c2c;
}
```
