using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Domain.Monitor;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Tests.Unit.Domain;

namespace Tests.Unit.Domain.Monitor;

public class ChatMonitorConversationContextTests
{
    [Fact]
    public async Task Monitor_InteractiveMessage_StampsOriginContextOnUserMessage()
    {
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "conv-1", channelId: "signalr", agentId: "jonas", sender: "test");
        var signalr = MonitorTestMocks.CreateChannel("signalr", message);
        var fakeAgent = MonitorTestMocks.CreateAgent();

        var monitor = new ChatMonitor(
            [signalr],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            MonitorTestMocks.CreateApprovalHandlerFactory(),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            new Mock<ILogger<ChatMonitor>>().Object);

        await monitor.Monitor(CancellationToken.None);

        fakeAgent.ReceivedMessages.TryDequeue(out var messages).ShouldBeTrue();
        var userMessage = messages!.ShouldHaveSingleItem();
        var context = userMessage.GetConversationContext().ShouldNotBeNull();
        context.AgentId.ShouldBe("jonas");
        context.ConversationId.ShouldBe("conv-1");
        context.UserId.ShouldBe("test");
        context.Origin.ShouldBe(new ReplyTarget("signalr", "conv-1"));
    }

    [Fact]
    public void BuildConversationContext_UsesFirstDeliveryTarget()
    {
        var channel = new FakeChannelConnection { ChannelId = "telegram" };
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "fire-1", channelId: "scheduling", agentId: "jonas");
        var targets = new[] { new ChatMonitor.DeliveryTarget(channel, "t-9") };

        var context = ChatMonitor.BuildConversationContext(message, targets);

        context.ConversationId.ShouldBe("t-9");
        context.Origin.ShouldBe(new ReplyTarget("telegram", "t-9"));
    }

    [Fact]
    public void BuildConversationContext_NoTargets_FallsBackToMessageOrigin()
    {
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "conv-2", channelId: "voice", agentId: "jonas") with
        { SatelliteId = "fran-office-01" };

        var context = ChatMonitor.BuildConversationContext(message, []);

        context.ConversationId.ShouldBe("conv-2");
        context.Origin.ShouldBe(new ReplyTarget("voice", "conv-2", "fran-office-01"));
    }
}