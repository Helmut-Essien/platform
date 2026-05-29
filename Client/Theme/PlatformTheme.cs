using MudBlazor;

namespace Platform.Client.Theme;

public static class PlatformTheme
{
    public static MudTheme Theme { get; } = new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = "#5c9f24",
            Secondary = "#5c9f24",
            Tertiary = "#5c9f24",
            Info = "#5c9f24",
            Success = "#5c9f24",
            Warning = "#5c9f24",
            Error = "#5c9f24",
            Dark = "#5c9f24",
            Black = "#121212",
            Background = "#121212",
            Surface = "#1e1e1e",
            DrawerBackground = "#1e1e1e",
            AppbarBackground = "#121212",
            TextPrimary = "#ededed",
            TextSecondary = "#a0a0a0",
            TextDisabled = "rgba(160,160,160,0.5)",
            Divider = "#2c2c2c",
            LinesDefault = "#2c2c2c",
            TableLines = "#2c2c2c",
            TableHover = "#2a2a2a",
            ActionDefault = "#5c9f24",
            ActionDisabled = "rgba(160,160,160,0.3)"
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px"
        }
    };
}
