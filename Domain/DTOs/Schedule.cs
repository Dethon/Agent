using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record Schedule
{
    public required string Id { get; init; }
    public required AgentDefinition Agent { get; init; }
    public required string Prompt { get; init; }
    public string? CronExpression { get; init; }
    public DateTime? RunAt { get; init; }
    public required ScheduleTarget Target { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastRunAt { get; init; }
    public DateTime? NextRunAt { get; init; }
}

[PublicAPI]
public record ScheduleTarget
{
    public required string Channel { get; init; }
    public long? ChatId { get; init; }
    public long? ThreadId { get; init; }
    public string? UserId { get; init; }
    public string? BotTokenHash { get; init; }
}

[PublicAPI]
public record ScheduleSummary(
    string Id,
    string AgentName,
    string Prompt,
    string? CronExpression,
    DateTime? RunAt,
    DateTime? NextRunAt,
    string Channel);
