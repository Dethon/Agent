using Domain.DTOs.Metrics.Enums;

namespace Domain.DTOs.Metrics;

public record VoiceEvent : MetricEvent
{
    public required VoiceMetric Metric { get; init; }
    public string? SatelliteId { get; init; }
    public string? Room { get; init; }
    public string? Identity { get; init; }
    public string? Outcome { get; init; }
    public string? Priority { get; init; }
    public long? DurationMs { get; init; }
    public double? Confidence { get; init; }
    public double? Similarity { get; init; }
    public string? Error { get; init; }
    public double? PeakRms { get; init; }
    public long? SpeechMs { get; init; }
    public double? FloorRms { get; init; }
    public double? TrailingRms { get; init; }
    public string? EndReason { get; init; }
    public double? AvgLogProb { get; init; }
    public double? NoSpeechProb { get; init; }
    public double? CompressionRatio { get; init; }
}