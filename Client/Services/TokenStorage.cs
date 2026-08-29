using Microsoft.JSInterop;

namespace Platform.Client.Services;

public class TokenStorage(IJSRuntime js)
{
    public const string TokenKey = "platform.auth.token";
    public const string EmailKey = "platform.auth.email";

    public async Task<string?> GetTokenAsync() =>
        await js.InvokeAsync<string?>("sessionStorage.getItem", TokenKey);

    public async Task SetAsync(string token, string email)
    {
        await js.InvokeVoidAsync("sessionStorage.setItem", TokenKey, token);
        await js.InvokeVoidAsync("sessionStorage.setItem", EmailKey, email);
    }

    public async Task ClearAsync()
    {
        await js.InvokeVoidAsync("sessionStorage.removeItem", TokenKey);
        await js.InvokeVoidAsync("sessionStorage.removeItem", EmailKey);
    }

    public async Task<string?> GetEmailAsync() =>
        await js.InvokeAsync<string?>("sessionStorage.getItem", EmailKey);
}
