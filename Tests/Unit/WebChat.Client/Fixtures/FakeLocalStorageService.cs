using WebChat.Client.Contracts;

namespace Tests.Unit.WebChat.Client.Fixtures;

public sealed class FakeLocalStorageService(CallRecorder? recorder = null) : ILocalStorageService
{
    private readonly Dictionary<string, string> _values = new();

    public IReadOnlyDictionary<string, string> Values => _values;

    public FakeLocalStorageService Seed(string key, string value)
    {
        _values[key] = value;
        return this;
    }

    public ValueTask<string?> GetAsync(string key)
    {
        recorder?.Record($"storage-get:{key}");
        return ValueTask.FromResult(_values.GetValueOrDefault(key));
    }

    public ValueTask SetAsync(string key, string value)
    {
        recorder?.Record($"storage-set:{key}");
        _values[key] = value;
        return ValueTask.CompletedTask;
    }
}