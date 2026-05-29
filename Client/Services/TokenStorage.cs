using Microsoft.JSInterop;

namespace Platform.Client.Services;

public class TokenStorage(IJSRuntime js)
{
    public const string TokenKey = "platform.auth.token";
    public const string EmailKey = "platform.auth.email";

    public async Task<string?> GetTokenAsync() =>
        await js.InvokeAsync<string?>("localStorage.getItem", TokenKey);

    public async Task SetAsync(string token, string email)
    {
        await js.InvokeVoidAsync("localStorage.setItem", TokenKey, token);
        await js.InvokeVoidAsync("localStorage.setItem", EmailKey, email);
    }

    public async Task ClearAsync()
    {
        await js.InvokeVoidAsync("localStorage.removeItem", TokenKey);
        await js.InvokeVoidAsync("localStorage.removeItem", EmailKey);
    }

    public async Task<string?> GetEmailAsync() =>
        await js.InvokeAsync<string?>("localStorage.getItem", EmailKey);
}
