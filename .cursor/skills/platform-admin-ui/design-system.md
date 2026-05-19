# Design system — Platform Admin UI

Portfolio source: [helmut-essien.github.io](https://helmut-essien.github.io/portfolio/) (dark + lime green).

## CSS custom properties

```css
:root {
  --primary: #71e215;
  --primary-hover: #5c9f24;
  --accent: #FFCC00;
  --background: #000000;
  --surface: #0c1408;
  --surface-alt: #232323;
  --surface-elevated: #1a1a1a;
  --text-primary: #ffffff;
  --text-secondary: rgba(255, 255, 255, 0.7);
  --text-body: #FAF5E9;
  --text-muted: rgba(250, 245, 233, 0.6);
  --success: #71e215;
  --warning: #FFCC00;
  --suspended: #f59e0b;
  --error: #ef4444;
  --info: #60a5fa;
  --border-color: #333333;
  --card-shadow-glow: 0 0 20px rgba(113, 226, 21, 0.3);
}
```

## Typography

| Role | Font | Weights | Size notes |
|------|------|---------|------------|
| UI | **Inter** | 400, 500, 600, 700 | Body min 14px |
| Code | **JetBrains Mono** | 400, 500 | Keys/JSON 13px+ |

### Font loading (`Client/wwwroot/index.html`)

```html
<link rel="preconnect" href="https://fonts.googleapis.com" />
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin />
<link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&family=JetBrains+Mono:wght@400;500&display=swap" rel="stylesheet" />
```

### Global CSS (`Client/wwwroot/css/app.css`)

```css
html, body {
  font-family: 'Inter', system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif;
  background-color: var(--background, #000000);
  color: var(--text-body, #FAF5E9);
}

.license-key,
.text-code,
.mud-code-block {
  font-family: 'JetBrains Mono', ui-monospace, monospace;
}

.license-key {
  color: var(--primary, #71e215);
  background: var(--surface-elevated, #1a1a1a);
  padding: 0.25rem 0.5rem;
  border-radius: 4px;
  border: 1px solid var(--border-color, #333333);
}

.btn-action {
  background-color: #71e215;
  color: #000000;
  font-weight: 600;
  border: 2px solid transparent;
  border-radius: 30px;
  padding: 10px 20px;
  transition: 0.3s;
}

.btn-action:hover {
  background-color: transparent;
  border-color: #FFCC00;
  color: #ffffff;
}

.mud-card-hover-glow:hover {
  box-shadow: var(--card-shadow-glow);
}

*:focus-visible {
  outline: 2px solid #FFCC00;
  outline-offset: 2px;
}
```

## MudTheme (register in `Client/Program.cs`)

```csharp
using MudBlazor;

public static class PlatformTheme
{
    public static MudTheme Dark = new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = "#71e215",
            Secondary = "#5c9f24",
            Tertiary = "#FFCC00",
            Info = "#60a5fa",
            Success = "#71e215",
            Warning = "#f59e0b",
            Error = "#ef4444",
            Black = "#000000",
            Background = "#000000",
            Surface = "#0c1408",
            DrawerBackground = "#0c1408",
            AppbarBackground = "#000000",
            TextPrimary = "#ffffff",
            TextSecondary = "rgba(255,255,255,0.7)",
            TextDisabled = "rgba(250,245,233,0.6)",
            Divider = "#333333",
            LinesDefault = "#333333",
            TableLines = "#333333",
            TableHover = "#1a1a1a",
            ActionDefault = "#71e215",
            ActionDisabled = "rgba(255,255,255,0.3)",
        },
        Typography = new Typography
        {
            Default = new Default { FontFamily = new[] { "Inter", "system-ui", "sans-serif" } },
            H1 = new H1 { FontWeight = 700, FontSize = "2rem" },
            H2 = new H2 { FontWeight = 600, FontSize = "1.5rem" },
            H3 = new H3 { FontWeight = 600, FontSize = "1.25rem" },
            Body1 = new Body1 { FontSize = "0.875rem", FontWeight = 400 },
            Body2 = new Body2 { FontSize = "0.8125rem", FontWeight = 400 },
            Button = new Button { FontWeight = 600, TextTransform = "none" },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
        },
    };
}
```

```csharp
// Program.cs
builder.Services.AddMudServices();
// In App.razor or root layout:
// <MudThemeProvider Theme="PlatformTheme.Dark" IsDarkMode="true" />
```

**Filled primary button:** Mud uses `Primary` — set button text to dark where contrast fails:

```razor
<MudButton Variant="Variant.Filled" Color="Color.Primary" Class="btn-action">Issue License</MudButton>
```

## Contrast (WCAG 2.1 AA)

| Pair | Ratio | Pass |
|------|-------|------|
| `#71e215` on `#000000` | ~7.2:1 | AAA |
| `#FAF5E9` on `#0c1408` | ~12:1 | AAA |
| `rgba(255,255,255,0.7)` on `#000000` | ~10:1 | AAA |
| `#000000` on `#71e215` (button text) | verify at build | Use for CTA label |

## Spacing

- Content padding: 24px
- Grid gutter: 24px (`Spacing="6"` in MudGrid)
- Card padding: 16–24px
- Dialog padding: 24px

## Elevation

- Cards: border `1px solid #333` before shadow
- Hover: `--card-shadow-glow` only (no heavy Material elevation)
