using Infrastructure.Clients.Cli;

namespace Tests.Unit.Infrastructure.Cli;

internal sealed class FakeTerminalAdapter : ITerminalAdapter
{
    private readonly List<ChatLine[]> _displayedMessages = [];
    private readonly List<string> _systemMessages = [];
    private readonly List<(string ToolName, IReadOnlyDictionary<string, object?> Arguments)> _autoApprovedTools = [];

    public event Action<string>? InputReceived;
    public event Action? ShutdownRequested;

    public bool IsStarted { get; private set; }
    public bool IsStopped { get; private set; }
    public bool IsCleared { get; private set; }
    public IReadOnlyList<ChatLine[]> DisplayedMessages => _displayedMessages;
    public IReadOnlyList<string> SystemMessages => _systemMessages;

    public IReadOnlyList<(string ToolName, IReadOnlyDictionary<string, object?> Arguments)> AutoApprovedTools =>
        _autoApprovedTools;

    public bool NextApprovalResult { get; set; } = true;

    public void Start()
    {
        IsStarted = true;
    }

    public void Stop()
    {
        IsStopped = true;
    }

    public void DisplayMessage(ChatLine[] lines)
    {
        _displayedMessages.Add(lines);
    }

    public void ClearDisplay()
    {
        IsCleared = true;
        _displayedMessages.Clear();
    }

    public void ShowSystemMessage(string message)
    {
        _systemMessages.Add(message);
    }

    public void ShowAutoApprovedTool(string toolName, IReadOnlyDictionary<string, object?> arguments)
    {
        _autoApprovedTools.Add((toolName, arguments));
    }

    public Task<bool> ShowApprovalDialogAsync(string toolName, string details, CancellationToken cancellationToken)
    {
        return Task.FromResult(NextApprovalResult);
    }

    public void SimulateInput(string input)
    {
        InputReceived?.Invoke(input);
    }

    public void SimulateShutdown()
    {
        ShutdownRequested?.Invoke();
    }

    public void Dispose() { }
}