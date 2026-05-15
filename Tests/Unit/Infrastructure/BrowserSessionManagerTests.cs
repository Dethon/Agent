using Infrastructure.Clients.Browser;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Playwright;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class BrowserSessionManagerTests
{
    private static (Mock<IBrowserContext> Context, Mock<IPage> Page) CreateMocks()
    {
        var page = new Mock<IPage>();
        page.SetupGet(p => p.IsClosed).Returns(false);
        page.Setup(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()))
            .Returns(Task.CompletedTask);

        var ctx = new Mock<IBrowserContext>();
        ctx.Setup(c => c.NewPageAsync()).ReturnsAsync(page.Object);
        return (ctx, page);
    }

    [Fact]
    public async Task PruneIdleAsync_RemovesSessionsOlderThanThreshold()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (ctx, page) = CreateMocks();
        await using var manager = new BrowserSessionManager(
            timeProvider: time,
            idleTimeout: TimeSpan.FromMinutes(30));

        await manager.GetOrCreateAsync("s1", ctx.Object);
        time.Advance(TimeSpan.FromMinutes(31));

        await manager.PruneIdleAsync();

        manager.Get("s1").ShouldBeNull();
        page.Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Once);
    }

    [Fact]
    public async Task PruneIdleAsync_KeepsRecentlyAccessedSessions()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (ctx, page) = CreateMocks();
        await using var manager = new BrowserSessionManager(
            timeProvider: time,
            idleTimeout: TimeSpan.FromMinutes(30));

        await manager.GetOrCreateAsync("s1", ctx.Object);
        time.Advance(TimeSpan.FromMinutes(20));

        await manager.PruneIdleAsync();

        manager.Get("s1").ShouldNotBeNull();
        page.Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Never);
    }

    [Fact]
    public async Task PruneIdleAsync_AfterAccess_ResetsIdleClock()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (ctx, page) = CreateMocks();
        await using var manager = new BrowserSessionManager(
            timeProvider: time,
            idleTimeout: TimeSpan.FromMinutes(30));

        await manager.GetOrCreateAsync("s1", ctx.Object);
        time.Advance(TimeSpan.FromMinutes(25));

        // Re-access just before threshold
        await manager.GetOrCreateAsync("s1", ctx.Object);
        time.Advance(TimeSpan.FromMinutes(10));

        await manager.PruneIdleAsync();

        // Total elapsed since last access = 10 min < 30 min threshold
        manager.Get("s1").ShouldNotBeNull();
        page.Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Never);
    }

    [Fact]
    public async Task PruneIdleAsync_OnlyRemovesIdleSessions()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (ctx1, page1) = CreateMocks();
        var (ctx2, page2) = CreateMocks();
        await using var manager = new BrowserSessionManager(
            timeProvider: time,
            idleTimeout: TimeSpan.FromMinutes(30));

        await manager.GetOrCreateAsync("idle", ctx1.Object);
        time.Advance(TimeSpan.FromMinutes(25));
        await manager.GetOrCreateAsync("active", ctx2.Object);
        time.Advance(TimeSpan.FromMinutes(10));

        await manager.PruneIdleAsync();

        manager.Get("idle").ShouldBeNull();
        manager.Get("active").ShouldNotBeNull();
        page1.Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Once);
        page2.Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Never);
    }

    [Fact]
    public async Task BackgroundTimer_FiresPrune_OnPruneInterval()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (ctx, page) = CreateMocks();
        await using var manager = new BrowserSessionManager(
            timeProvider: time,
            idleTimeout: TimeSpan.FromMinutes(30),
            pruneInterval: TimeSpan.FromMinutes(5));

        await manager.GetOrCreateAsync("s1", ctx.Object);
        time.Advance(TimeSpan.FromMinutes(31));

        // Wait for the scheduled timer callback to run
        await Task.Delay(100);

        manager.Get("s1").ShouldBeNull();
        page.Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Once);
    }
}