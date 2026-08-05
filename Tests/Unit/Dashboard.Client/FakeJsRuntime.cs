using Microsoft.JSInterop;

namespace Tests.Unit.Dashboard.Client;

// The browser edge of the dashboard client: a dictionary standing in for localStorage, so
// LocalStorageService stays real and no storage interface has to exist for a test to reach it.
public sealed class FakeJsRuntime : IJSRuntime
{
    public Dictionary<string, string> Storage { get; } = [];

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        ValueTask.FromResult(Invoke<TValue>(identifier, args));

    public ValueTask<TValue> InvokeAsync<TValue>(
        string identifier, CancellationToken cancellationToken, object?[]? args) =>
        ValueTask.FromResult(Invoke<TValue>(identifier, args));

    private TValue Invoke<TValue>(string identifier, object?[]? args)
    {
        var key = (string)args![0]!;

        switch (identifier)
        {
            case "localStorage.getItem":
                return Storage.TryGetValue(key, out var value) ? (TValue)(object)value : default!;
            case "localStorage.setItem":
                Storage[key] = (string)args[1]!;
                return default!;
            default:
                throw new NotSupportedException(identifier);
        }
    }
}