using Domain.DTOs;

namespace Infrastructure.CliGui.Abstractions;

public interface IToolApprovalUi
{
    void ShowToolResult(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        ToolApprovalResult resultType);

    Task<ToolApprovalResult> ShowApprovalDialogAsync(
        string toolName,
        string details,
        CancellationToken cancellationToken);
}