using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Voice;

public record VoiceState
{
    public IReadOnlyList<VoiceEvent> Events { get; init; } = [];
    public VoiceDimension GroupBy { get; init; } = VoiceDimension.SatelliteId;
    public VoiceMetric Metric { get; init; } = VoiceMetric.UtteranceTranscribed;
    public Dictionary<string, decimal> Breakdown { get; init; } = [];
    public DateOnly From { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly To { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
}