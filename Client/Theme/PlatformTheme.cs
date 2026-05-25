using MudBlazor;

namespace Platform.Client.Theme;

public static class PlatformTheme
{
    public static MudTheme Theme { get; } = new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = "#71e215",
            Secondary = "#5c9f24",
            Background = "#000000",
            Surface = "#0c1408",
            AppbarBackground = "#000000",
            DrawerBackground = "#0c1408",
            TextPrimary = "#FAF5E9",
            TextSecondary = "rgba(255,255,255,0.7)",
            Success = "#71e215",
            Warning = "#FFCC00",
            Error = "#ef4444",
            Info = "#60a5fa"
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px"
        }
    };
}
