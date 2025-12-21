using Domain.DTOs;

namespace Infrastructure.Clients.Cli;

public interface ITerminalAdapter : IDisposable
{
    event Action<string>? InputReceived;
    event Action? ShutdownRequested;

    void Start();
    void Stop();
    void DisplayMessage(ChatLine[] lines);
    void ClearDisplay();
    void ShowSystemMessage(string message);
    void ShowToolResult(string toolName, IReadOnlyDictionary<string, object?> arguments, ToolApprovalResult resultType);

    Task<ToolApprovalResult> ShowApprovalDialogAsync(string toolName, string details,
        CancellationToken cancellationToken);
}