using McpChannelVoice.Services;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class ActiveAlertRegistryTests
{
    [Fact]
    public void Acknowledge_OnAnyTargetedSatellite_CancelsTheSharedAlert()
    {
        var registry = new ActiveAlertRegistry();
        using var cts = new CancellationTokenSource();
        var handle = new AlertHandle(cts, ["kitchen-01", "bedroom-01"]);
        registry.Register(handle);

        var acknowledged = registry.Acknowledge("bedroom-01");

        acknowledged.ShouldBeTrue();
        handle.IsAcknowledged.ShouldBeTrue();
        handle.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void Acknowledge_RemovesEveryTargetEntry_SoASecondAckIsANoOp()
    {
        var registry = new ActiveAlertRegistry();
        using var cts = new CancellationTokenSource();
        registry.Register(new AlertHandle(cts, ["kitchen-01", "bedroom-01"]));

        registry.Acknowledge("kitchen-01").ShouldBeTrue();
        registry.Acknowledge("bedroom-01").ShouldBeFalse(); // already cleared by the first ack
    }

    [Fact]
    public void Acknowledge_UnknownSatellite_ReturnsFalse()
    {
        var registry = new ActiveAlertRegistry();

        registry.Acknowledge("ghost").ShouldBeFalse();
    }

    [Fact]
    public void Discard_RemovesEntries_WithoutAcknowledging()
    {
        var registry = new ActiveAlertRegistry();
        using var cts = new CancellationTokenSource();
        var handle = new AlertHandle(cts, ["kitchen-01"]);
        registry.Register(handle);

        registry.Discard(handle);

        registry.Acknowledge("kitchen-01").ShouldBeFalse();
        handle.IsAcknowledged.ShouldBeFalse();
    }
}