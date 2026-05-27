using System.Text.Json.Serialization;
using Domain.DTOs.Metrics.Enums;

namespace Domain.DTOs.Metrics;

public record VoiceEvent : MetricEvent
{
    [JsonConverter(typeof(JsonStringEnumConverter<VoiceMetric>))]
    public required VoiceMetric Metric { get; init; }
    public string? SatelliteId { get; init; }
    public string? Room { get; init; }
    public string? Identity { get; init; }
    public string? WakeWord { get; init; }
    public string? Language { get; init; }
    public string? SttProvider { get; init; }
    public string? SttModel { get; init; }
    public string? TtsProvider { get; init; }
    public string? TtsVoice { get; init; }
    public string? Outcome { get; init; }
    public string? Source { get; init; }
    public string? Priority { get; init; }
    public long? DurationMs { get; init; }
    public double? AudioSeconds { get; init; }
    public double? Confidence { get; init; }
    public string? Error { get; init; }
}