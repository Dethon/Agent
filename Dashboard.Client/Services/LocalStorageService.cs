using Microsoft.JSInterop;

namespace Dashboard.Client.Services;

public sealed class LocalStorageService(IJSRuntime js)
{
    public async ValueTask<string?> GetAsync(string key) =>
        await js.InvokeAsync<string?>("localStorage.getItem", key);

    public async ValueTask SetAsync(string key, string value) =>
        await js.InvokeVoidAsync("localStorage.setItem", key, value);

    public async ValueTask<T?> GetAsync<T>(string key) where T : struct, Enum
    {
        var raw = await GetAsync(key);
        return Enum.TryParse<T>(raw, out var val) ? val : null;
    }

    public async ValueTask<int?> GetIntAsync(string key)
    {
        var raw = await GetAsync(key);
        return int.TryParse(raw, out var val) ? val : null;
    }

    public async Task<string?> GetStringAsync(string key) =>
        await js.InvokeAsync<string?>("localStorage.getItem", key);
}