namespace Domain.DTOs;

public sealed record ParsedServiceBusMessage(
    string Prompt,
    string Sender,
    string SourceId,
    string AgentId);
