using System.Text.Json;
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
        sender.Verify(
            s => s.SendAsync("OnAgentsUpdated", It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void McpRun_AcceptsAgentClientSerializationFormat()
    {
        // Mirror exactly what McpChannelConnection.RegisterAgentsAsync emits:
        // JsonSerializer.Serialize over IReadOnlyList<AgentCatalogEntry> with default (PascalCase) options.
        IReadOnlyList<AgentCatalogEntry> catalogIn =
            [new AgentCatalogEntry("jonas", "Jonas", "general"), new AgentCatalogEntry("jack", "Jack", null)];
        var wire = JsonSerializer.Serialize(catalogIn);

        var catalog = new MutableAgentCatalog();
        var sender = new Mock<IHubNotificationSender>();
        var tool = new RegisterAgentsTool(catalog, sender.Object);

        tool.McpRun(wire);

        catalog.GetAll().Count.ShouldBe(2);
        catalog.Get("jonas")!.Name.ShouldBe("Jonas");
        catalog.Get("jack")!.Description.ShouldBeNull();
    }
}