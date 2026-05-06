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

    [Fact]
    public void TryGet_KnownKey_ReturnsTrueAndManager()
    {
        var reg = new SubAgentSessionsRegistry(MakeFactory());
        var key = new AgentKey("c1", "agent1");
        var created = reg.GetOrCreate(key);

        reg.TryGet(key, out var sessions).ShouldBeTrue();
        sessions.ShouldBeSameAs(created);
    }

    [Fact]
    public void TryGet_UnknownKey_ReturnsFalse()
    {
        var reg = new SubAgentSessionsRegistry(MakeFactory());
        reg.TryGet(new AgentKey("nope", "x"), out var sessions).ShouldBeFalse();
        sessions.ShouldBeNull();
    }

    private static Func<AgentKey, SubAgentSessionManager> MakeFactory() =>
        _ => new SubAgentSessionManager(
            agentFactory: _ => throw new NotImplementedException(),
            replyToConversationId: "c1",
            replyChannel: null);
}
