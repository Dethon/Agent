using System.Text.Json;
using Domain.Channels;
using Domain.DTOs.Channel;
using Infrastructure.McpTools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Infrastructure.McpTools;

public class ChannelReceiveToolTests
{
    // Pins the clamp structurally: an unclamped maxWaitMs beyond ChannelProtocol.DefaultReceiveWaitMs
    // would park a subscriber's poll past ChannelProtocol.LiveSubscriberFreshness's 2x headroom,
    // making a genuinely live subscriber read as dead. Advancing time just past the clamp ceiling
    // (but nowhere near the caller-requested wait) must resolve the call with an empty batch — if
    // the clamp weren't applied, the call would still be parked waiting for the full 90s.
    [Fact]
    public async Task McpRun_MaxWaitBeyondDefaultCeiling_IsClampedAndTimesOutEarly()
    {
        var time = new FakeTimeProvider();
        var inbox = new ChannelInbox(time);
        var services = new ServiceCollection().AddSingleton(inbox).BuildServiceProvider();

        var call = ChannelReceiveTool.McpRun("sess-1", 90_000, services, CancellationToken.None);

        await Task.Delay(50);
        time.Advance(TimeSpan.FromMilliseconds(ChannelProtocol.DefaultReceiveWaitMs + 1_000));

        var json = await call.WaitAsync(TimeSpan.FromSeconds(5));
        var result = JsonSerializer.Deserialize<ChannelReceiveResult>(json, ChannelProtocol.SerializerOptions)!;

        result.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task McpRun_NegativeMaxWait_ClampsToZeroAndReturnsImmediately()
    {
        var inbox = new ChannelInbox();
        var services = new ServiceCollection().AddSingleton(inbox).BuildServiceProvider();

        var json = await ChannelReceiveTool.McpRun("sess-1", -1, services, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var result = JsonSerializer.Deserialize<ChannelReceiveResult>(json, ChannelProtocol.SerializerOptions)!;

        result.Items.ShouldBeEmpty();
    }
}