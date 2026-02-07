namespace Domain.DTOs;

public record ToolApprovalRequest(
    string? MessageId,
    string ToolName,
    IReadOnlyDictionary<string, object?> Arguments);