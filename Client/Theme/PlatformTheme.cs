using MudBlazor;

namespace Platform.Client.Theme;

public static class PlatformTheme
{
    public static MudTheme Theme { get; } = new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = "#92d959",
            Secondary = "#5c9f24",
            Tertiary = "#5c9f24",
            Info = "#5c9f24",
            Success = "#92d959",
            Warning = "#e6b84d",
            Error = "#ffb4ab",
            Dark = "#5c9f24",
            Black = "#10150c",
            Background = "#10150c",
            Surface = "#1e1e1e",
            DrawerBackground = "#191d14",
            AppbarBackground = "#10150c",
            TextPrimary = "#ededed",
            TextSecondary = "#a0a0a0",
            TextDisabled = "rgba(160,160,160,0.5)",
            Divider = "#2c2c2c",
            LinesDefault = "#2c2c2c",
            TableLines = "#2c2c2c",
            TableHover = "#272b22",
            ActionDefault = "#92d959",
            ActionDisabled = "rgba(160,160,160,0.3)"
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "2px"
        }
    };
}
