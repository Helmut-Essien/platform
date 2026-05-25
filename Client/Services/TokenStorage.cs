using Blazored.LocalStorage;

namespace Platform.Client.Services;

public class TokenStorage(ILocalStorageService localStorage)
{
    public const string TokenKey = "platform.auth.token";
    public const string EmailKey = "platform.auth.email";

    public async Task<string?> GetTokenAsync() =>
        await localStorage.GetItemAsync<string>(TokenKey);

    public async Task SetAsync(string token, string email)
    {
        await localStorage.SetItemAsync(TokenKey, token);
        await localStorage.SetItemAsync(EmailKey, email);
    }

    public async Task ClearAsync()
    {
        await localStorage.RemoveItemAsync(TokenKey);
        await localStorage.RemoveItemAsync(EmailKey);
    }

    public async Task<string?> GetEmailAsync() =>
        await localStorage.GetItemAsync<string>(EmailKey);
}
