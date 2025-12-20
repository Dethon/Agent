namespace Domain.DTOs;

public record ToolApprovalRequest(
    string ToolName,
    IReadOnlyDictionary<string, object?> Arguments);