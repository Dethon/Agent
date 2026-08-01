using Shouldly;
using WebChat.Client.State;
using WebChat.Client.State.AgentSettings;

namespace Tests.Unit.WebChat.Client.State;

public class AgentSettingsStoreTests
{
    [Fact]
    public void SetAgentModel_NewAgent_AddsEntry()
    {
        var dispatcher = new Dispatcher();
        var store = new AgentSettingsStore(dispatcher);

        dispatcher.Dispatch(new SetAgentModel("jack", "z-ai/glm-5.2"));

        store.State.ByAgent["jack"].Model.ShouldBe("z-ai/glm-5.2");
    }

    [Fact]
    public void SetAgentReasoningEffort_ExistingAgent_KeepsModel()
    {
        var dispatcher = new Dispatcher();
        var store = new AgentSettingsStore(dispatcher);
        dispatcher.Dispatch(new SetAgentModel("jack", "z-ai/glm-5.2"));

        dispatcher.Dispatch(new SetAgentReasoningEffort("jack", "high"));

        store.State.ByAgent["jack"].ShouldBe(new AgentModelSettings("z-ai/glm-5.2", "high"));
    }

    [Fact]
    public void AgentSettingsLoaded_ReplacesAgentEntry()
    {
        var dispatcher = new Dispatcher();
        var store = new AgentSettingsStore(dispatcher);

        dispatcher.Dispatch(new AgentSettingsLoaded("jack", new AgentModelSettings("z-ai/glm-5.2", "max")));

        store.State.ByAgent["jack"].ShouldBe(new AgentModelSettings("z-ai/glm-5.2", "max"));
    }
}