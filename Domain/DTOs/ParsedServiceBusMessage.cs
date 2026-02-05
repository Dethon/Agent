namespace Domain.DTOs;

public sealed record ParsedServiceBusMessage(
    string CorrelationId,
    string AgentId,
    string Prompt,
    string Sender);
