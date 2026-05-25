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

public class ChatMonitorScheduleMetricsTests
{
    [Fact]
    public void BuildScheduleEvent_WithScheduleOrigin_ReturnsEvent()
    {
        var msg = new ChannelMessage
        {
            ConversationId = "c", Content = "do the thing", Sender = "scheduler", ChannelId = "scheduling",
            AgentId = "jonas", Origin = new MessageOrigin(MessageOriginKind.Schedule, "morning-news")
        };

        var evt = ChatMonitor.BuildScheduleEvent(msg, durationMs: 1234, success: true, error: null);

        evt.ShouldNotBeNull();
        evt.ScheduleId.ShouldBe("morning-news");
        evt.AgentId.ShouldBe("jonas");
        evt.Prompt.ShouldBe("do the thing");
        evt.DurationMs.ShouldBe(1234);
        evt.Success.ShouldBeTrue();
    }

    [Fact]
    public void BuildScheduleEvent_WithNonScheduleMessage_ReturnsNull()
    {
        var msg = new ChannelMessage { ConversationId = "c", Content = "hi", Sender = "u", ChannelId = "signalr" };
        ChatMonitor.BuildScheduleEvent(msg, 1, true, null).ShouldBeNull();
    }

    [Fact]
    public async Task Monitor_ScheduledMessage_SuccessfulRun_PublishesSuccessMetricOnce()
    {
        var scheduledMessage = ScheduledMessage();
        var scheduling = MonitorTestMocks.CreateChannel("scheduling", scheduledMessage);
        var published = CapturePublishedMetrics(out var metricsPublisher);

        var monitor = BuildMonitor([scheduling], metricsPublisher);
        await monitor.Monitor(CancellationToken.None);

        var evt = published.OfType<ScheduleExecutionEvent>().ShouldHaveSingleItem();
        evt.ScheduleId.ShouldBe("morning-news");
        evt.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task Monitor_ScheduledMessage_OneDeliveryTargetFails_DeliversToOthersAndEmitsMetricOnce()
    {
        // The bad target is listed first so that, without per-target isolation, it would
        // abort the fan-out before the healthy target is ever reached.
        var scheduledMessage = ScheduledMessage(
            new ReplyTarget("bad", "b"), new ReplyTarget("good", "g"));
        var scheduling = MonitorTestMocks.CreateChannel("scheduling", scheduledMessage);

        var bad = new Mock<IChannelConnection>();
        bad.SetupGet(c => c.ChannelId).Returns("bad");
        bad.SetupGet(c => c.Messages).Returns(AsyncEnumerable.Empty<ChannelMessage>());
        bad.Setup(c => c.SendReplyAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ReplyContentType>(),
                It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("channel down"));
        var good = MonitorTestMocks.CreateChannel("good");

        var published = CapturePublishedMetrics(out var metricsPublisher);

        var monitor = BuildMonitor([scheduling, bad.Object, good], metricsPublisher);
        await monitor.Monitor(CancellationToken.None);

        // The healthy target still receives the stream-complete reply.
        good.SentReplies.ShouldContain(r => r.ContentType == ReplyContentType.StreamComplete && r.IsComplete);
        // The failed delivery is surfaced rather than swallowed.
        published.OfType<ErrorEvent>().ShouldNotBeEmpty();
        // The schedule execution metric is still emitted exactly once, as a success.
        var evt = published.OfType<ScheduleExecutionEvent>().ShouldHaveSingleItem();
        evt.Success.ShouldBeTrue();
    }

    private static ChannelMessage ScheduledMessage(params ReplyTarget[] replyTo) => new()
    {
        ConversationId = "fire-1",
        Content = "do the thing",
        Sender = "scheduler",
        ChannelId = "scheduling",
        AgentId = "jonas",
        Origin = new MessageOrigin(MessageOriginKind.Schedule, "morning-news"),
        ReplyTo = replyTo.Length > 0 ? replyTo : null
    };

    private static List<MetricEvent> CapturePublishedMetrics(out Mock<IMetricsPublisher> metricsPublisher)
    {
        var published = new List<MetricEvent>();
        metricsPublisher = new Mock<IMetricsPublisher>();
        metricsPublisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => published.Add(e))
            .Returns(Task.CompletedTask);
        return published;
    }

    private static ChatMonitor BuildMonitor(
        IReadOnlyList<IChannelConnection> channels, Mock<IMetricsPublisher> metricsPublisher) => new(
        channels,
        MonitorTestMocks.CreateAgentFactory(MonitorTestMocks.CreateAgent()),
        MonitorTestMocks.CreateApprovalHandlerFactory(),
        MonitorTestMocks.CreateThreadResolver(),
        metricsPublisher.Object,
        null,
        new Mock<ILogger<ChatMonitor>>().Object);
}