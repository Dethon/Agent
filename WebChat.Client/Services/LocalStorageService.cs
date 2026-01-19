using Microsoft.JSInterop;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public sealed class LocalStorageService(IJSRuntime js) : ILocalStorageService
{
    public ValueTask<string?> GetAsync(string key)
    {
        return js.InvokeAsync<string?>("localStorage.getItem", key);
    }

    public ValueTask SetAsync(string key, string value)
    {
        return js.InvokeVoidAsync("localStorage.setItem", key, value);
    }

    public ValueTask RemoveAsync(string key)
    {
        return js.InvokeVoidAsync("localStorage.removeItem", key);
    }
}