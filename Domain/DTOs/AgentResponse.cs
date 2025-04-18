namespace Domain.DTOs;

public record AgentResponse : ToolRequestMessage
{
    public required StopReason StopReason { get; init; }
};