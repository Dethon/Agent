using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Monitor;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Monitor;

// A chat command is not a turn, so a group whose first message is one runs no turn and builds
// nothing: no agent, no thread read, no MCP connection, no delivery target and no minted
// conversation. Clearing a conversation that has no live group is routine after a restart.
public class ChatMonitorConversationGroupTests
{
    [Fact]
    public async Task Monitor_GroupOpenedByAClearCommand_ConstructsNoAgentAndStillWipesTheThread()
    {
        var stateStore = new Mock<IThreadStateStore>();
        stateStore.Setup(s => s.DeleteAsync(It.IsAny<AgentKey>())).Returns(Task.CompletedTask);
        var threadResolver = new ChatThreadResolver(stateStore.Object);
        var agentKey = new AgentKey("conv-1");
        threadResolver.Resolve(agentKey);
        var channel = MonitorTestMocks.CreateChannel(
            messages: MonitorTestMocks.CreateChannelMessage(conversationId: "conv-1", content: "/clear"));
        var agentFactory = MonitorTestMocks.CreateAgentFactory(MonitorTestMocks.CreateAgent());

        var monitor = new ChatMonitor(
            [channel],
            agentFactory,
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            Mock.Of<ILogger<ChatMonitor>>());

        await monitor.Monitor(CancellationToken.None);

        agentFactory.Created.ShouldBeEmpty();
        stateStore.Verify(s => s.DeleteAsync(agentKey), Times.Once);
    }

    [Fact]
    public async Task Monitor_GroupOpenedByAClearCommandCarryingReplyTo_ResolvesNoTargetsAndMintsNothing()
    {
        // Resolving this message's targets would mint a WebChat conversation for a command
        // nobody will ever write a reply into.
        var signalr = new FakeChannelConnection { ChannelId = "signalr", ConversationIdToReturn = "minted-signalr" };
        signalr.Complete();
        var scheduling = MonitorTestMocks.CreateChannel("scheduling", new ChannelMessage
        {
            ConversationId = "fire-1",
            Content = "/clear",
            Sender = "fran",
            ChannelId = "scheduling",
            AgentId = "jonas",
            ReplyTo = [new ReplyTarget("signalr", null)]
        });
        var agentFactory = MonitorTestMocks.CreateAgentFactory(MonitorTestMocks.CreateAgent());

        var monitor = new ChatMonitor(
            [scheduling, signalr],
            agentFactory,
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            null,
            Mock.Of<ILogger<ChatMonitor>>());

        await monitor.Monitor(CancellationToken.None);

        signalr.CreatedConversations.ShouldBeEmpty();
        agentFactory.Created.ShouldBeEmpty();
    }

    [Fact]
    public async Task Monitor_TurnAfterAClearCommand_IsRoutedToTheChannelThatSentIt()
    {
        // The clear ends the group, so the message typed next opens a fresh one and is its
        // first turn. This is the case the deleted message-index invariant used to cover: a
        // command torn-down group must not leave its anchors — here the voice satellite that
        // sent the clear — pointing at the next turn's reply.
        var voice = new FakeChannelConnection { ChannelId = "voice" };
        var webchat = new FakeChannelConnection { ChannelId = "webchat" };
        var stateStore = new Mock<IThreadStateStore>();
        stateStore.Setup(s => s.DeleteAsync(It.IsAny<AgentKey>())).Returns(() =>
        {
            // Written from inside the delete so the ordering needs no gate: by the time the
            // clear reaches its persisted state, the group it ended is already complete.
            webchat.WriteMessage(MonitorTestMocks.CreateChannelMessage(
                conversationId: "7:42", content: "and now the news", channelId: "webchat", agentId: "jonas"));
            webchat.Complete();
            voice.Complete();
            return Task.CompletedTask;
        });
        var threadResolver = new ChatThreadResolver(stateStore.Object);
        voice.WriteMessage(MonitorTestMocks.CreateChannelMessage(
            conversationId: "7:42", content: "/clear", channelId: "voice", agentId: "jonas"));

        var monitor = new ChatMonitor(
            [voice, webchat],
            MonitorTestMocks.CreateAgentFactory(MonitorTestMocks.CreateAgent()),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            Mock.Of<ILogger<ChatMonitor>>());

        await monitor.Monitor(CancellationToken.None);

        webchat.SentReplies.ShouldContain(r => r.ContentType == ReplyContentType.StreamComplete && r.IsComplete);
        voice.SentReplies.ShouldBeEmpty();
    }

    [Fact]
    public async Task Monitor_SecondTurnReusingTheGroupAnchors_AnnouncesTheConversationTheFirstTurnMinted()
    {
        // The minting turn announced itself through its own create_conversation, so it is
        // skipped. The second turn minted nothing, so the same conversation now pre-exists and
        // its live stream has to be set up again before the reply chunks arrive.
        var signalr = new FakeChannelConnection { ChannelId = "signalr", ConversationIdToReturn = "minted-signalr" };
        signalr.Complete();
        var scheduling = MonitorTestMocks.CreateChannel(
            "scheduling",
            ScheduleFire("Check stalled torrents"),
            ScheduleFire("Check them again"));

        var monitor = new ChatMonitor(
            [scheduling, signalr],
            MonitorTestMocks.CreateAgentFactory(MonitorTestMocks.CreateAgent()),
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            null,
            Mock.Of<ILogger<ChatMonitor>>());

        await monitor.Monitor(CancellationToken.None);

        signalr.CreatedConversations.Count.ShouldBe(2);
        signalr.CreatedConversations[0].ExistingConversationId.ShouldBeNull();
        signalr.CreatedConversations[1].ExistingConversationId.ShouldBe("minted-signalr");
    }

    [Fact]
    public async Task Monitor_FirstTurnFailsToEstablish_LogsEndsTheGroupAndTheNextMessageStartsAFreshOne()
    {
        // Establishing the group happens inside the turn loop now, so a state store that is
        // down has to end the group there and then: log the failure, complete the group and
        // dispose the agent. The channel stays open on purpose: a group that waited for its
        // message stream to end would hold the agent open with it — and the next message for
        // the same conversation must open a fresh group and be answered, not queue silently
        // into the dead one until restart.
        var channel = new FakeChannelConnection();
        var fakeAgent = new FakeAiAgent { RestoreExceptionToThrow = new HttpRequestException("state store down") };
        var logger = new Mock<ILogger<ChatMonitor>>();

        var monitor = new ChatMonitor(
            [channel],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            null,
            logger.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = monitor.Monitor(cts.Token);
        channel.WriteMessage(MonitorTestMocks.CreateChannelMessage());

        await fakeAgent.DisposeSignaled.Task.WaitAsync(cts.Token);
        fakeAgent.DisposeCalls.ShouldBe(1);
        logger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("conv-1")),
            It.IsAny<HttpRequestException>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);

        // The store recovered; the next message opens a fresh group and is answered.
        fakeAgent.RestoreExceptionToThrow = null;
        channel.WriteMessage(MonitorTestMocks.CreateChannelMessage(content: "again"));
        channel.Complete();
        await run;

        channel.SentReplies.ShouldContain(r => r.ContentType == ReplyContentType.StreamComplete && r.IsComplete);
    }

    private static ChannelMessage ScheduleFire(string content)
    {
        return new ChannelMessage
        {
            ConversationId = "fire-1",
            Content = content,
            Sender = "scheduler",
            ChannelId = "scheduling",
            AgentId = "jonas",
            Origin = new MessageOrigin(MessageOriginKind.Schedule, "sched-1"),
            ReplyTo = [new ReplyTarget("signalr", null)]
        };
    }
}