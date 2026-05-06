using Agent.Services.SubAgents;
using Domain.Agents;
using Shouldly;

namespace Tests.Unit.Agent;

public sealed class SubAgentSessionsRegistryTests
{
    [Fact]
    public void GetOrCreate_ReturnsSameInstanceForSameKey()
    {
        var reg = new SubAgentSessionsRegistry(MakeFactory());
        var a = reg.GetOrCreate(new AgentKey("c1", "agent1"));
        var b = reg.GetOrCreate(new AgentKey("c1", "agent1"));
        a.ShouldBeSameAs(b);
    }

    [Fact]
    public void GetOrCreate_DifferentKeys_AreIsolated()
    {
        var reg = new SubAgentSessionsRegistry(MakeFactory());
        var a = reg.GetOrCreate(new AgentKey("c1", "agent1"));
        var b = reg.GetOrCreate(new AgentKey("c2", "agent1"));
        a.ShouldNotBeSameAs(b);
    }

    private static Func<AgentKey, SubAgentSessionManager> MakeFactory() =>
        _ => new SubAgentSessionManager(
            agentFactory: _ => throw new NotImplementedException(),
            replyToConversationId: "c1",
            replyChannel: null);
}
