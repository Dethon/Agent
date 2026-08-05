using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Monitor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Tests.Unit.Domain;
using Xunit;

namespace Tests.Unit.Domain.Monitor;

// One turn has one conversation id. A schedule fire carries a synthetic ConversationId
// ("sched-...") and a ReplyTo entry with a null ConversationId, prompting the target
// channel to mint a fresh WebChat conversation. Everything the turn produces — the agent
// it is built from, the chat history it restores, and the events it publishes — must name
// the minted conversation, because that is the one an operator can open.
public class ChatMonitorDeliveryIdentityTests
{
    private static readonly AgentKey MintedKey = new("7:9", "jonas");

    private static readonly ToolApprovalRequest AnyRequest =
        new(null, "some_tool", new Dictionary<string, object?>());

    [Fact]
    public async Task Monitor_ScheduledMessageMintingConversation_BuildsTheWholeTurnFromTheMintedId()
    {
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
            ConversationIdToReturn = MintedKey.ConversationId
        };
        webchat.Complete();
        var fakeAgent = ReplyingAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);
        var published = new List<MetricEvent>();
        var recallHook = new RecordingRecallHook();

        var monitor = new ChatMonitor(
            [scheduling, webchat],
            agentFactory,
            MonitorTestMocks.CreateThreadResolver(),
            CapturingPublisher(published),
            recallHook,
            new Mock<ILogger<ChatMonitor>>().Object);

        await monitor.Monitor(CancellationToken.None);

        var created = agentFactory.Created.ShouldHaveSingleItem();
        created.Key.ShouldBe(MintedKey);
        fakeAgent.RestoredSessionKeys.ShouldHaveSingleItem().ShouldBe(MintedKey.ToString());
        FirstReplyOf(published).ConversationId.ShouldBe(MintedKey.ConversationId);
        // Recall provenance is durable: a memory extracted here records its source
        // conversation, and that has to be one somebody can still open months later.
        recallHook.ConversationIds.ShouldHaveSingleItem().ShouldBe(MintedKey.ConversationId);

        // The approval route the turn was built with reaches the minted channel under the
        // minted id — the conversation the answer lands in, not the scheduling origin
        // (which auto-approves silently and would hide the tool calls from the user).
        await created.ApprovalHandler.NotifyAutoApprovedAsync(
            created.Key.ConversationId, [AnyRequest], CancellationToken.None);
        webchat.NotifyAutoApprovedCalls.ShouldHaveSingleItem()
            .ConversationId.ShouldBe(MintedKey.ConversationId);
    }

    [Fact]
    public async Task Monitor_WebChatMessageWithoutReplyTo_BuildsTheWholeTurnFromItsOwnConversationId()
    {
        // The common path: no ReplyTo, so the delivery identity is the message's own
        // conversation id. This is the case that already works and must not regress.
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "42:13", channelId: "webchat", agentId: "jonas");
        var webchat = MonitorTestMocks.CreateChannel("webchat", message);
        var fakeAgent = ReplyingAgent();
        var agentFactory = MonitorTestMocks.CreateAgentFactory(fakeAgent);
        var published = new List<MetricEvent>();
        var recallHook = new RecordingRecallHook();

        var monitor = new ChatMonitor(
            [webchat],
            agentFactory,
            MonitorTestMocks.CreateThreadResolver(),
            CapturingPublisher(published),
            recallHook,
            new Mock<ILogger<ChatMonitor>>().Object);

        await monitor.Monitor(CancellationToken.None);

        var ownKey = new AgentKey("42:13", "jonas");
        agentFactory.Created.ShouldHaveSingleItem().Key.ShouldBe(ownKey);
        fakeAgent.RestoredSessionKeys.ShouldHaveSingleItem().ShouldBe(ownKey.ToString());
        FirstReplyOf(published).ConversationId.ShouldBe(ownKey.ConversationId);
        recallHook.ConversationIds.ShouldHaveSingleItem().ShouldBe(ownKey.ConversationId);
    }

    [Fact]
    public async Task Monitor_LaterTurnResolvingItsOwnTargets_AttributesFirstReplyToItsOwnConversation()
    {
        // The group's delivery key names the conversation the FIRST turn's reply landed in —
        // here the minted WebChat one. A later plain message joins the group under the group
        // key and is delivered back to its own origin, so its first-reply latency belongs to
        // the conversation its own reply landed in, not to the group's delivery key.
        var fire = new ChannelMessage
        {
            ConversationId = "sched-morning-news-12345",
            Content = "do the thing",
            Sender = "scheduler",
            ChannelId = "scheduling",
            AgentId = "jonas",
            Origin = new MessageOrigin(MessageOriginKind.Schedule, "morning-news"),
            ReplyTo = [new ReplyTarget("webchat", null)]
        };
        var followUp = new ChannelMessage
        {
            ConversationId = "sched-morning-news-12345",
            Content = "and again",
            Sender = "scheduler",
            ChannelId = "scheduling",
            AgentId = "jonas"
        };
        var scheduling = MonitorTestMocks.CreateChannel("scheduling", fire, followUp);
        var webchat = new FakeChannelConnection
        {
            ChannelId = "webchat",
            ConversationIdToReturn = MintedKey.ConversationId
        };
        webchat.Complete();
        var published = new List<MetricEvent>();

        var monitor = new ChatMonitor(
            [scheduling, webchat],
            MonitorTestMocks.CreateAgentFactory(ReplyingAgent()),
            MonitorTestMocks.CreateThreadResolver(),
            CapturingPublisher(published),
            null,
            new Mock<ILogger<ChatMonitor>>().Object);

        await monitor.Monitor(CancellationToken.None);

        var firstReplies = published.OfType<LatencyEvent>()
            .Where(e => e.Stage == LatencyStage.FirstReply)
            .Select(e => e.ConversationId)
            .ToList();
        firstReplies.ShouldBe([MintedKey.ConversationId, "sched-morning-news-12345"]);
    }

    private sealed class RecordingRecallHook : IMemoryRecallHook
    {
        public List<string?> ConversationIds { get; } = [];

        public Task EnrichAsync(
            ChatMessage message,
            string userId,
            string? conversationId,
            string? agentId,
            AgentSession thread,
            CancellationToken ct)
        {
            ConversationIds.Add(conversationId);
            return Task.CompletedTask;
        }
    }

    private static FakeAiAgent ReplyingAgent()
    {
        return new FakeAiAgent
        {
            UpdatesToYield = [new AgentResponseUpdate { Contents = [new TextContent("done")] }]
        };
    }

    private static IMetricsPublisher CapturingPublisher(List<MetricEvent> published)
    {
        var metrics = new Mock<IMetricsPublisher>();
        metrics.Setup(m => m.Publish(It.IsAny<MetricEvent>()))
            .Callback((MetricEvent e) => { lock (published) { published.Add(e); } });
        return metrics.Object;
    }

    private static LatencyEvent FirstReplyOf(List<MetricEvent> published)
    {
        return published.OfType<LatencyEvent>()
            .Where(e => e.Stage == LatencyStage.FirstReply)
            .ShouldHaveSingleItem();
    }
}