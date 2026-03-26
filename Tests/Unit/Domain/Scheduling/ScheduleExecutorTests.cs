using System.Threading.Channels;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Monitor;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Scheduling;

public class ScheduleExecutorTests
{
    private readonly Mock<IScheduleStore> _store = new();
    private readonly Mock<IScheduleAgentFactory> _agentFactory = new();
    private readonly Channel<Schedule> _scheduleChannel = Channel.CreateUnbounded<Schedule>();
    private readonly Mock<ILogger<ScheduleExecutor>> _logger = new();
    private readonly FakeAiAgent _fakeAgent = MonitorTestMocks.CreateAgent();

    private Schedule CreateSchedule(string? cronExpression = "0 9 * * *") => new()
    {
        Id = "sched_test",
        Agent = new AgentDefinition
        {
            Id = "jack",
            Name = "Jack",
            Model = "test",
            McpServerEndpoints = []
        },
        Prompt = "Test prompt",
        CronExpression = cronExpression,
        UserId = "user123",
        CreatedAt = DateTime.UtcNow,
        NextRunAt = DateTime.UtcNow.AddHours(1)
    };

    private ScheduleExecutor CreateExecutor(
        IReadOnlyList<IChannelConnection> channels,
        string? defaultScheduleChannelId = null)
    {
        _agentFactory
            .Setup(f => f.CreateFromDefinition(
                It.IsAny<AgentKey>(),
                It.IsAny<string>(),
                It.IsAny<AgentDefinition>(),
                It.IsAny<IToolApprovalHandler>()))
            .Returns(_fakeAgent);

        return new ScheduleExecutor(
            _store.Object,
            _agentFactory.Object,
            channels,
            defaultScheduleChannelId,
            (_, _) => new Mock<IToolApprovalHandler>().Object,
            _scheduleChannel,
            new Mock<IMetricsPublisher>().Object,
            _logger.Object);
    }

    [Fact]
    public async Task ProcessSchedule_CapableChannel_CreatesConversationAndSendsReplies()
    {
        // Arrange
        var channel = new FakeChannelConnection
        {
            ChannelId = "web",
            ConversationIdToReturn = "conv-123"
        };
        var executor = CreateExecutor([channel], "web");
        var schedule = CreateSchedule();

        _scheduleChannel.Writer.TryWrite(schedule);
        _scheduleChannel.Writer.Complete();

        // Act
        await executor.ProcessSchedulesAsync(CancellationToken.None);

        // Assert
        channel.CreatedConversations.ShouldContain(c =>
            c.AgentId == "jack" && c.Sender == "user123");
        channel.SentReplies.ShouldContain(r =>
            r.ContentType == ReplyContentType.StreamComplete && r.IsComplete);
    }

    [Fact]
    public async Task ProcessSchedule_ChannelUnavailable_RunsSilentlyAndDeletesOneShot()
    {
        // Scenario 1: No matching channel — wrong channel id
        var channel1 = new FakeChannelConnection
        {
            ChannelId = "telegram",
            ConversationIdToReturn = "conv-123"
        };
        var executor1 = CreateExecutor([channel1], "web");
        var schedule1 = CreateSchedule(cronExpression: null);

        _scheduleChannel.Writer.TryWrite(schedule1);
        _scheduleChannel.Writer.Complete();

        await executor1.ProcessSchedulesAsync(CancellationToken.None);

        channel1.CreatedConversations.ShouldBeEmpty();
        channel1.SentReplies.ShouldBeEmpty();
        _store.Verify(s => s.DeleteAsync("sched_test", It.IsAny<CancellationToken>()), Times.Once);
        _store.Invocations.Clear();

        // Scenario 2: Null default channel id
        var channel2 = new FakeChannelConnection
        {
            ChannelId = "web",
            ConversationIdToReturn = "conv-123"
        };
        var scheduleChannel2 = Channel.CreateUnbounded<Schedule>();
        var executor2 = new ScheduleExecutor(
            _store.Object, _agentFactory.Object, [channel2], null,
            (_, _) => new Mock<IToolApprovalHandler>().Object,
            scheduleChannel2, new Mock<IMetricsPublisher>().Object, _logger.Object);
        var schedule2 = CreateSchedule(cronExpression: null);

        scheduleChannel2.Writer.TryWrite(schedule2);
        scheduleChannel2.Writer.Complete();

        await executor2.ProcessSchedulesAsync(CancellationToken.None);

        channel2.CreatedConversations.ShouldBeEmpty();
        channel2.SentReplies.ShouldBeEmpty();
        _store.Verify(s => s.DeleteAsync("sched_test", It.IsAny<CancellationToken>()), Times.Once);
        _store.Invocations.Clear();

        // Scenario 3: CreateConversation returns null
        var channel3 = new FakeChannelConnection
        {
            ChannelId = "web",
            ConversationIdToReturn = null
        };
        var scheduleChannel3 = Channel.CreateUnbounded<Schedule>();
        var executor3 = new ScheduleExecutor(
            _store.Object, _agentFactory.Object, [channel3], "web",
            (_, _) => new Mock<IToolApprovalHandler>().Object,
            scheduleChannel3, new Mock<IMetricsPublisher>().Object, _logger.Object);
        var schedule3 = CreateSchedule(cronExpression: null);

        scheduleChannel3.Writer.TryWrite(schedule3);
        scheduleChannel3.Writer.Complete();

        await executor3.ProcessSchedulesAsync(CancellationToken.None);

        channel3.CreatedConversations.ShouldNotBeEmpty();
        channel3.SentReplies.ShouldBeEmpty();
        _store.Verify(s => s.DeleteAsync("sched_test", It.IsAny<CancellationToken>()), Times.Once);
        _store.Invocations.Clear();
        _agentFactory.Invocations.Clear();

        // Scenario 4: Empty channels list — agent should not be created
        var scheduleChannel4 = Channel.CreateUnbounded<Schedule>();
        var executor4 = new ScheduleExecutor(
            _store.Object, _agentFactory.Object, [], null,
            (_, _) => new Mock<IToolApprovalHandler>().Object,
            scheduleChannel4, new Mock<IMetricsPublisher>().Object, _logger.Object);
        var schedule4 = CreateSchedule(cronExpression: null);

        scheduleChannel4.Writer.TryWrite(schedule4);
        scheduleChannel4.Writer.Complete();

        await executor4.ProcessSchedulesAsync(CancellationToken.None);

        _agentFactory.Verify(
            f => f.CreateFromDefinition(
                It.IsAny<AgentKey>(), It.IsAny<string>(),
                It.IsAny<AgentDefinition>(), It.IsAny<IToolApprovalHandler>()),
            Times.Never);
        _store.Verify(s => s.DeleteAsync("sched_test", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessSchedule_RecurringSchedule_NotDeleted()
    {
        // Arrange
        var channel = new FakeChannelConnection { ChannelId = "web", ConversationIdToReturn = null };
        var executor = CreateExecutor([channel], "web");
        var schedule = CreateSchedule(cronExpression: "0 9 * * *");

        _scheduleChannel.Writer.TryWrite(schedule);
        _scheduleChannel.Writer.Complete();

        // Act
        await executor.ProcessSchedulesAsync(CancellationToken.None);

        // Assert
        _store.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}