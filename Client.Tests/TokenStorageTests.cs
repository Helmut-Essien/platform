using Microsoft.JSInterop;
using Platform.Client.Services;
using Xunit;

namespace Client.Tests;

public class TokenStorageTests
{
    [Fact]
    public async Task GetToken_ReadsSessionStorageOnceThenServesFromMemory()
    {
        var js = new CountingJs();
        js.Store[TokenStorage.TokenKey] = "t1";
        var storage = new TokenStorage(js);

        Assert.Equal("t1", await storage.GetTokenAsync());
        Assert.Equal("t1", await storage.GetTokenAsync());
        Assert.Null(await storage.GetEmailAsync());

        Assert.Equal(2, js.Reads); // token + email on first load only
    }

    [Fact]
    public async Task SetAndClear_UpdateCacheAndSessionStorage()
    {
        var js = new CountingJs();
        var storage = new TokenStorage(js);

        await storage.SetAsync("t2", "a@b.c");
        Assert.Equal("t2", await storage.GetTokenAsync());
        Assert.Equal("a@b.c", await storage.GetEmailAsync());
        Assert.Equal("t2", js.Store[TokenStorage.TokenKey]);

        await storage.ClearAsync();
        Assert.Null(await storage.GetTokenAsync());
        Assert.Empty(js.Store);
        Assert.Equal(0, js.Reads);
    }

    private sealed class CountingJs : IJSRuntime
    {
        public Dictionary<string, string> Store { get; } = new();
        public int Reads { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args)
        {
            var key = (string)args![0]!;
            switch (identifier)
            {
                case "sessionStorage.setItem": Store[key] = (string)args[1]!; break;
                case "sessionStorage.removeItem": Store.Remove(key); break;
                case "sessionStorage.getItem":
                    Reads++;
                    return new ValueTask<TValue>((TValue)(object?)(Store.TryGetValue(key, out var v) ? v : null)!);
            }
            return new ValueTask<TValue>(default(TValue)!);
        }
    }
}
