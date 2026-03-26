using System.Text.Json.Serialization;

namespace Domain.DTOs.Metrics;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TokenUsageEvent), "token_usage")]
[JsonDerivedType(typeof(ToolCallEvent), "tool_call")]
[JsonDerivedType(typeof(ErrorEvent), "error")]
[JsonDerivedType(typeof(ScheduleExecutionEvent), "schedule_execution")]
[JsonDerivedType(typeof(HeartbeatEvent), "heartbeat")]
public abstract record MetricEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? AgentId { get; init; }
    public string? ConversationId { get; init; }
}