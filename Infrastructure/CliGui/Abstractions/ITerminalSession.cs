using Infrastructure.CliGui.Rendering;

namespace Infrastructure.CliGui.Abstractions;

public interface ITerminalSession : IDisposable
{
    event Action<string>? InputReceived;
    event Action? ShutdownRequested;

    void Start();
    void Stop();

    void DisplayMessage(ChatLine[] lines);
    void ClearDisplay();
    void ShowSystemMessage(string message);
}