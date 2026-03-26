using Domain.Contracts;
using Domain.DTOs.Metrics;
using Infrastructure.Metrics;
using Moq;

namespace Tests.Unit.Infrastructure.Metrics;

public class HeartbeatServiceTests
{
    [Fact]
    public async Task ExecuteAsync_publishes_heartbeat_event_with_service_name()
    {
        var publisher = new Mock<IMetricsPublisher>();
        var sut = new HeartbeatService(publisher.Object, "test-service");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await sut.StartAsync(cts.Token);
        await Task.Delay(50);
        await sut.StopAsync(CancellationToken.None);

        publisher.Verify(p => p.PublishAsync(
            It.Is<HeartbeatEvent>(e => e.Service == "test-service"),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }
}