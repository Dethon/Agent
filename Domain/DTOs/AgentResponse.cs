namespace Domain.DTOs;

public record AgentResponse : Message
{
    public required StopReason StopReason { get; init; }
};