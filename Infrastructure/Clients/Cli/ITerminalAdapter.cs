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
    Task<bool> ShowApprovalDialogAsync(string toolName, string details, CancellationToken cancellationToken);
}