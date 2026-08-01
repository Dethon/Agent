using System.Text.Json;
using Domain.DTOs.Channel;
using WebChat.Client.Contracts;
using WebChat.Client.State.AgentSettings;

namespace WebChat.Client.State.Effects;

public sealed class AgentSettingsEffect : IDisposable
{
    private const string KeyPrefix = "agentConfigPatch:";

    private readonly IDisposable _subscription;
    private readonly ILocalStorageService _localStorage;
    private IReadOnlyDictionary<string, AgentModelSettings> _previous;

    public AgentSettingsEffect(AgentSettingsStore store, ILocalStorageService localStorage)
    {
        _localStorage = localStorage;
        _previous = store.State.ByAgent;
        _subscription = store.StateObservable.Subscribe(HandleStateChange);
    }

    public static async Task LoadAsync(
        IReadOnlyList<AgentCatalogEntry> agents, ILocalStorageService localStorage, IDispatcher dispatcher)
    {
        foreach (var agent in agents)
        {
            var stored = await localStorage.GetAsync($"{KeyPrefix}{agent.Id}");
            var settings = Deserialize(stored) ?? new AgentModelSettings(null, null);
            dispatcher.Dispatch(new AgentSettingsLoaded(
                agent.Id, AgentSettingsSelectors.Sanitize(settings, agent)));
        }
    }

    private void HandleStateChange(AgentSettingsState state)
    {
        var changed = state.ByAgent
            .Where(kv => !Equals(_previous.GetValueOrDefault(kv.Key), kv.Value))
            .ToList();
        _previous = state.ByAgent;

        changed.ForEach(kv =>
            _ = _localStorage.SetAsync($"{KeyPrefix}{kv.Key}", JsonSerializer.Serialize(kv.Value)));
    }

    private static AgentModelSettings? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AgentModelSettings>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _subscription.Dispose();
}