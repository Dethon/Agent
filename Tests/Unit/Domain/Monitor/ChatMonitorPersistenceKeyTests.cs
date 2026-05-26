using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.Monitor;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Tests.Unit.Domain;
using Xunit;

namespace Tests.Unit.Domain.Monitor;

public class ChatMonitorPersistenceKeyTests
{
    [Fact]
    public async Task Monitor_ScheduledMessageMintingConversation_RestoresThreadWithMintedId()
    {
        // A schedule fire carries a synthetic ConversationId ("sched-...") and a ReplyTo
        // entry with a null ConversationId, prompting the target channel to mint a fresh
        // WebChat conversation. The agent's chat-history persistence key must be the
        // minted id — otherwise WebChat (which reads history under the minted
        // {chatId}:{threadId} key) shows the new conversation as empty.
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var scheduleMessage = new ChannelMessage
        {
            ConversationId = "sched-morning-news-12345",
            Content = "do the thing",
            Sender = "scheduler",
            ChannelId = "scheduling",
            AgentId = "jonas",
            Origin = new MessageOrigin(MessageOriginKind.Schedule, "morning-news"),
            ReplyTo = [new ReplyTarget("webchat", null)]
        };
        var scheduling = MonitorTestMocks.CreateChannel("scheduling", scheduleMessage);
        var webchat = new FakeChannelConnection
        {
            ChannelId = "webchat",
            ConversationIdToReturn = "7:9"
        };
        webchat.Complete();
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);

        var monitor = new ChatMonitor(
            [scheduling, webchat],
            agentFactory,
            MonitorTestMocks.CreateApprovalHandlerFactory(),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            new Mock<ILogger<ChatMonitor>>().Object);

        await monitor.Monitor(CancellationToken.None);

        var restoredKey = fakeAgent.RestoredSessionKeys.ShouldHaveSingleItem();
        restoredKey.ShouldBe(new AgentKey("7:9", "jonas").ToString());
    }

    [Fact]
    public async Task Monitor_WebChatMessageWithoutReplyTo_RestoresThreadWithOriginalConversationId()
    {
        // Normal WebChat (no ReplyTo) must keep using the message's own ConversationId
        // as the persistence key — this is the case that already works today and we
        // must not regress.
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "42:13", channelId: "webchat", agentId: "jonas");
        var webchat = MonitorTestMocks.CreateChannel("webchat", message);
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);

        var monitor = new ChatMonitor(
            [webchat],
            agentFactory,
            MonitorTestMocks.CreateApprovalHandlerFactory(),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            new Mock<ILogger<ChatMonitor>>().Object);

        await monitor.Monitor(CancellationToken.None);

        var restoredKey = fakeAgent.RestoredSessionKeys.ShouldHaveSingleItem();
        restoredKey.ShouldBe(new AgentKey("42:13", "jonas").ToString());
    }
}