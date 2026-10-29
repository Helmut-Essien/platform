using Microsoft.JSInterop;

namespace Platform.Client.Services;

/// <summary>
/// Session token store. Singleton per tab: values are cached in memory after the first read so
/// every API request (JwtAuthorizationMessageHandler) and auth-state check does not pay a JS interop
/// round-trip. sessionStorage stays the source of truth across reloads (it is per tab anyway).
/// </summary>
public class TokenStorage(IJSRuntime js)
{
    public const string TokenKey = "platform.auth.token";
    public const string EmailKey = "platform.auth.email";

    private bool _loaded;
    private string? _token;
    private string? _email;

    public async Task<string?> GetTokenAsync()
    {
        await EnsureLoadedAsync();
        return _token;
    }

    public async Task<string?> GetEmailAsync()
    {
        await EnsureLoadedAsync();
        return _email;
    }

    public async Task SetAsync(string token, string email)
    {
        await js.InvokeVoidAsync("sessionStorage.setItem", TokenKey, token);
        await js.InvokeVoidAsync("sessionStorage.setItem", EmailKey, email);
        _token = token;
        _email = email;
        _loaded = true;
    }

    public async Task ClearAsync()
    {
        // Clear the cache first so a failing interop call cannot leave a signed-out tab sending the old token.
        _token = null;
        _email = null;
        _loaded = true;
        await js.InvokeVoidAsync("sessionStorage.removeItem", TokenKey);
        await js.InvokeVoidAsync("sessionStorage.removeItem", EmailKey);
    }

    private async Task EnsureLoadedAsync()
    {
        if (_loaded)
            return;
        var token = await js.InvokeAsync<string?>("sessionStorage.getItem", TokenKey);
        var email = await js.InvokeAsync<string?>("sessionStorage.getItem", EmailKey);
        if (_loaded)
            return; // a concurrent Set/Clear won the race; keep its values
        _token = token;
        _email = email;
        _loaded = true;
    }
}
