using Domain.Agents;
using Shouldly;

namespace Tests.Unit.Domain;

public class AgentKeyTests
{
    [Fact]
    public void ToString_WithAgentId_FormatsCorrectly()
    {
        var key = new AgentKey("conv-123", "jack");
        key.ToString().ShouldBe("agent-key:jack:conv-123");
    }

    [Fact]
    public void ToString_WithoutAgentId_FormatsCorrectly()
    {
        var key = new AgentKey("conv-123");
        key.ToString().ShouldBe("agent-key::conv-123");
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        new AgentKey("conv-123", "jack").ShouldBe(new AgentKey("conv-123", "jack"));
    }

    [Fact]
    public void Equality_DifferentConversationId_AreNotEqual()
    {
        new AgentKey("conv-123", "jack").ShouldNotBe(new AgentKey("conv-456", "jack"));
    }
}
