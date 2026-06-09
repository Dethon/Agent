using System.Text.Json;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.Metrics;

public class VoiceEventTests
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void VoiceEvent_SerializesWithTypeDiscriminator()
    {
        MetricEvent evt = new VoiceEvent
        {
            Metric = VoiceMetric.WakeTriggered,
            SatelliteId = "kitchen-01",
            Room = "Kitchen",
            Identity = "household"
        };

        var json = JsonSerializer.Serialize(evt, _options);

        json.ShouldContain("\"type\":\"voice\"");
        json.ShouldContain($"\"metric\":{(int)VoiceMetric.WakeTriggered}");
        json.ShouldContain("\"satelliteId\":\"kitchen-01\"");
    }

    [Fact]
    public void VoiceEvent_RoundTripsThroughBaseType()
    {
        MetricEvent evt = new VoiceEvent
        {
            Metric = VoiceMetric.SttLatencyMs,
            SatelliteId = "kitchen-01",
            Room = "Kitchen",
            DurationMs = 320
        };

        var json = JsonSerializer.Serialize(evt, _options);
        var decoded = JsonSerializer.Deserialize<MetricEvent>(json, _options);

        decoded.ShouldBeOfType<VoiceEvent>();
        ((VoiceEvent)decoded!).Metric.ShouldBe(VoiceMetric.SttLatencyMs);
        ((VoiceEvent)decoded!).DurationMs.ShouldBe(320);
    }
}