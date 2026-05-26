using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using McpServerScheduling.Services;
using McpServerScheduling.Settings;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Xunit;

namespace Tests.Unit.McpServerScheduling;

public class ScheduleDispatcherServiceTests
{
    [Fact]
    public async Task DispatchDueAsync_WhenEmitFails_DoesNotMutateStore()
    {
        var oneShot = OneShot();
        var store = StoreWithDue(oneShot);
        var emitter = Emitter(delivers: false);

        await BuildDispatcher(store.Object, emitter).DispatchDueAsync(CancellationToken.None);

        store.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(
            s => s.UpdateLastRunAsync(
                It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DispatchDueAsync_WhenEmitSucceeds_DeletesOneShotSchedule()
    {
        var store = StoreWithDue(OneShot());
        var emitter = Emitter(delivers: true);

        await BuildDispatcher(store.Object, emitter).DispatchDueAsync(CancellationToken.None);

        store.Verify(s => s.DeleteAsync("once", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchDueAsync_WhenEmitSucceeds_AdvancesRecurringSchedule()
    {
        var store = StoreWithDue(Recurring());
        var next = new DateTime(2026, 5, 26, 8, 0, 0, DateTimeKind.Utc);
        var cron = new Mock<ICronValidator>();
        cron.Setup(c => c.GetNextOccurrence("0 8 * * *", It.IsAny<DateTime>())).Returns(next);

        await BuildDispatcher(store.Object, Emitter(delivers: true), cron.Object).DispatchDueAsync(CancellationToken.None);

        store.Verify(s => s.UpdateLastRunAsync("daily", It.IsAny<DateTime?>(), next, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchDueAsync_NoActiveSessions_DoesNotQueryStore()
    {
        var store = new Mock<IScheduleStore>();
        var emitter = new Mock<IScheduleNotificationEmitter>();
        emitter.SetupGet(e => e.HasActiveSessions).Returns(false);

        await BuildDispatcher(store.Object, emitter.Object).DispatchDueAsync(CancellationToken.None);

        store.Verify(s => s.GetDueSchedulesAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ResolveInterval_NonPositive_ClampsToOneSecond(int seconds) =>
        ScheduleDispatcherService.ResolveInterval(seconds).ShouldBe(TimeSpan.FromSeconds(1));

    [Fact]
    public void ResolveInterval_Positive_IsUnchanged() =>
        ScheduleDispatcherService.ResolveInterval(30).ShouldBe(TimeSpan.FromSeconds(30));

    private static Schedule OneShot() =>
        new() { Id = "once", AgentId = "jack", Prompt = "p", RunAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow };

    private static Schedule Recurring() =>
        new() { Id = "daily", AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow };

    private static Mock<IScheduleStore> StoreWithDue(Schedule schedule)
    {
        var store = new Mock<IScheduleStore>();
        store.Setup(s => s.GetDueSchedulesAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([schedule]);
        return store;
    }

    private static IScheduleNotificationEmitter Emitter(bool delivers)
    {
        var emitter = new Mock<IScheduleNotificationEmitter>();
        emitter.SetupGet(e => e.HasActiveSessions).Returns(true);
        emitter.Setup(e => e.EmitAsync(It.IsAny<ChannelMessageNotification>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(delivers);
        return emitter.Object;
    }

    private static ScheduleDispatcherService BuildDispatcher(
        IScheduleStore store, IScheduleNotificationEmitter emitter, ICronValidator? cron = null) =>
        new(
            store,
            cron ?? new Mock<ICronValidator>().Object,
            emitter,
            new SchedulingSettings { RedisConnectionString = "x", DefaultDeliverTo = ["signalr"] },
            new Mock<ILogger<ScheduleDispatcherService>>().Object);
}