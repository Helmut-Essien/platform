using MudBlazor;

namespace Platform.Client.Services;

/// <summary>
/// Shared MudBlazor dialog sizing. Provider defaults to <see cref="Form"/>; use <see cref="Confirm"/> or <see cref="KeyReveal"/> where narrower modals are appropriate.
/// </summary>
public static class PlatformDialogOptions
{
    /// <summary>Create/edit forms — ~960px max on desktop.</summary>
    public static DialogOptions Form { get; } = new()
    {
        MaxWidth = MaxWidth.Medium,
        FullWidth = true
    };

    /// <summary>Short confirmation prompts.</summary>
    public static DialogOptions Confirm { get; } = new()
    {
        MaxWidth = MaxWidth.Small,
        FullWidth = true
    };

    /// <summary>One-time key reveal; non-dismissible backdrop.</summary>
    public static DialogOptions KeyReveal { get; } = new()
    {
        MaxWidth = MaxWidth.Small,
        FullWidth = true,
        CloseOnEscapeKey = false,
        BackdropClick = false
    };
}
