using System.Text.Json;
using Domain.DTOs.Channel;
using Shouldly;
using WebChat.Client.Contracts;
using WebChat.Client.State;
using WebChat.Client.State.AgentSettings;
using WebChat.Client.State.Effects;

namespace Tests.Unit.WebChat.Client.State;

public sealed class AgentSettingsEffectTests
{
    private static readonly AgentCatalogEntry _jack = new(
        "jack", "Jack", null,
        "openai/gpt-5.6-luna", "low",
        [new PatchableModel("openai/gpt-5.6-luna", "GPT Luna"), new PatchableModel("z-ai/glm-5.2", "GLM 5.2")],
        AgentConfigPatch.SupportedEfforts);

    private static IDispatcher CreateCapturingDispatcher(List<IAction> dispatched) =>
        new CapturingDispatcher(dispatched);

    [Fact]
    public async Task LoadAsync_StoredSettings_SanitizesAndDispatchesLoaded()
    {
        var storage = new FakeLocalStorage();
        await storage.SetAsync("agentConfigPatch:jack",
            """{"Model":"z-ai/glm-5.2","ReasoningEffort":"turbo"}""");
        var dispatched = new List<IAction>();
        var dispatcher = CreateCapturingDispatcher(dispatched);

        await AgentSettingsEffect.LoadAsync([_jack], storage, dispatcher);

        dispatched.OfType<AgentSettingsLoaded>().ShouldHaveSingleItem()
            .ShouldBe(new AgentSettingsLoaded("jack", new AgentModelSettings("z-ai/glm-5.2", "low")));
    }

    [Fact]
    public async Task LoadAsync_NothingStored_DispatchesDefaults()
    {
        var storage = new FakeLocalStorage();
        var dispatched = new List<IAction>();
        var dispatcher = CreateCapturingDispatcher(dispatched);

        await AgentSettingsEffect.LoadAsync([_jack], storage, dispatcher);

        dispatched.OfType<AgentSettingsLoaded>().ShouldHaveSingleItem()
            .ShouldBe(new AgentSettingsLoaded("jack", new AgentModelSettings("openai/gpt-5.6-luna", "low")));
    }

    [Fact]
    public async Task StateChange_ChangedEntry_PersistsToStorage()
    {
        var storage = new FakeLocalStorage();
        var dispatcher = new Dispatcher();
        var store = new AgentSettingsStore(dispatcher);
        using var effect = new AgentSettingsEffect(store, storage);

        dispatcher.Dispatch(new SetAgentModel("jack", "z-ai/glm-5.2"));
        await Task.Delay(50); // fire-and-forget write

        (await storage.GetAsync("agentConfigPatch:jack"))
            .ShouldBe(JsonSerializer.Serialize(new AgentModelSettings("z-ai/glm-5.2", null)));
    }

    private sealed class CapturingDispatcher(List<IAction> dispatched) : IDispatcher
    {
        public void Dispatch<TAction>(TAction action) where TAction : IAction => dispatched.Add(action);
    }

    private sealed class FakeLocalStorage : ILocalStorageService
    {
        private readonly Dictionary<string, string> _values = new();

        public ValueTask<string?> GetAsync(string key) =>
            ValueTask.FromResult(_values.GetValueOrDefault(key));

        public ValueTask SetAsync(string key, string value)
        {
            _values[key] = value;
            return ValueTask.CompletedTask;
        }
    }
}