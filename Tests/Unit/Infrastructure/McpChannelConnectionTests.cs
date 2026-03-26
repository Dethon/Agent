using System.Text.Json;
using Infrastructure.Clients.Channels;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class McpChannelConnectionTests
{
    [Fact]
    public async Task HandleNotification_WritesChannelMessage()
    {
        var sut = new McpChannelConnection("ch-1");
        var payload = JsonSerializer.SerializeToElement(new
        {
            conversationId = "conv-42",
            content = "Hello from MCP",
            sender = "user@test",
            agentId = "agent-7"
        });

        sut.HandleChannelMessageNotification(payload);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var messages = sut.Messages;
        await foreach (var msg in messages.WithCancellation(cts.Token))
        {
            msg.ConversationId.ShouldBe("conv-42");
            msg.Content.ShouldBe("Hello from MCP");
            msg.Sender.ShouldBe("user@test");
            msg.ChannelId.ShouldBe("ch-1");
            msg.AgentId.ShouldBe("agent-7");
            break;
        }
    }

    [Fact]
    public async Task HandleNotification_WithoutAgentId_DefaultsToNull()
    {
        var sut = new McpChannelConnection("ch-2");
        var payload = JsonSerializer.SerializeToElement(new
        {
            conversationId = "conv-99",
            content = "No agent",
            sender = "anon"
        });

        sut.HandleChannelMessageNotification(payload);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var msg in sut.Messages.WithCancellation(cts.Token))
        {
            msg.AgentId.ShouldBeNull();
            msg.ConversationId.ShouldBe("conv-99");
            break;
        }
    }
}