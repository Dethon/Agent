using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.Channel;
using McpChannelSignalR.McpTools;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

public class RegisterAgentsToolTests
{
    [Fact]
    public void McpRun_ReplacesCatalog_AndBroadcastsUpdate()
    {
        var catalog = new MutableAgentCatalog();
        var sender = new Mock<IHubNotificationSender>();
        var tool = new RegisterAgentsTool(catalog, sender.Object);

        var result = tool.McpRun(
            """[{"id":"jonas","name":"Jonas","description":"general"}]""");

        result.ShouldBe("registered 1 agents");
        catalog.Exists("jonas").ShouldBeTrue();
        sender.Verify(
            s => s.SendAsync("OnAgentsUpdated", It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void McpRun_WithEmptyArray_ClearsCatalog()
    {
        var catalog = new MutableAgentCatalog();
        catalog.Replace([new AgentCatalogEntry("old", "Old", null)]);
        var sender = new Mock<IHubNotificationSender>();
        var tool = new RegisterAgentsTool(catalog, sender.Object);

        var result = tool.McpRun("[]");

        result.ShouldBe("registered 0 agents");
        catalog.GetAll().ShouldBeEmpty();
    }
}