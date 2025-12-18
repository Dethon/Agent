namespace Infrastructure.Clients.Cli;

internal interface ITerminalAdapter : IDisposable
{
    event Action<string>? InputReceived;
    event Action? ShutdownRequested;

    void Start();
    void Stop();
    void DisplayMessage(ChatLine[] lines);
    void ClearDisplay();
    void ShowSystemMessage(string message);
}