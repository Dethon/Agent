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
    public async Task ProcessSchedule_NoMatchingChannel_RunsSilently()
    {
        // Arrange
        var channel = new FakeChannelConnection
        {
            ChannelId = "telegram",
            ConversationIdToReturn = "conv-123"
        };
        var executor = CreateExecutor([channel], "web");
        var schedule = CreateSchedule(cronExpression: null);

        _scheduleChannel.Writer.TryWrite(schedule);
        _scheduleChannel.Writer.Complete();

        // Act
        await executor.ProcessSchedulesAsync(CancellationToken.None);

        // Assert - no conversation created, no replies sent
        channel.CreatedConversations.ShouldBeEmpty();
        channel.SentReplies.ShouldBeEmpty();
        // One-shot schedule should be deleted
        _store.Verify(s => s.DeleteAsync("sched_test", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessSchedule_CreateConversationReturnsNull_RunsSilently()
    {
        // Arrange
        var channel = new FakeChannelConnection
        {
            ChannelId = "web",
            ConversationIdToReturn = null
        };
        var executor = CreateExecutor([channel], "web");
        var schedule = CreateSchedule(cronExpression: null);

        _scheduleChannel.Writer.TryWrite(schedule);
        _scheduleChannel.Writer.Complete();

        // Act
        await executor.ProcessSchedulesAsync(CancellationToken.None);

        // Assert
        channel.CreatedConversations.ShouldNotBeEmpty(); // attempted creation
        channel.SentReplies.ShouldBeEmpty(); // but ran silently
        _store.Verify(s => s.DeleteAsync("sched_test", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessSchedule_NullDefaultChannelId_RunsSilently()
    {
        // Arrange
        var channel = new FakeChannelConnection
        {
            ChannelId = "web",
            ConversationIdToReturn = "conv-123"
        };
        var executor = CreateExecutor([channel], defaultScheduleChannelId: null);
        var schedule = CreateSchedule(cronExpression: null);

        _scheduleChannel.Writer.TryWrite(schedule);
        _scheduleChannel.Writer.Complete();

        // Act
        await executor.ProcessSchedulesAsync(CancellationToken.None);

        // Assert
        channel.CreatedConversations.ShouldBeEmpty();
        channel.SentReplies.ShouldBeEmpty();
        _store.Verify(s => s.DeleteAsync("sched_test", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessSchedule_EmptyChannelsList_SkipsExecutionAndDeletesOneShot()
    {
        // Arrange — no channels at all
        var executor = CreateExecutor([], defaultScheduleChannelId: null);
        var schedule = CreateSchedule(cronExpression: null);

        _scheduleChannel.Writer.TryWrite(schedule);
        _scheduleChannel.Writer.Complete();

        // Act
        await executor.ProcessSchedulesAsync(CancellationToken.None);

        // Assert — agent should NOT have been created (no channel to run against)
        _agentFactory.Verify(
            f => f.CreateFromDefinition(
                It.IsAny<AgentKey>(),
                It.IsAny<string>(),
                It.IsAny<AgentDefinition>(),
                It.IsAny<IToolApprovalHandler>()),
            Times.Never);
        // One-shot schedule still deleted
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
