using Infrastructure.Clients.Cli;

namespace Tests.Unit.Infrastructure.Cli;

internal sealed class FakeTerminalAdapter : ITerminalAdapter
{
    private readonly List<ChatLine[]> _displayedMessages = [];
    private readonly List<string> _systemMessages = [];

    private readonly List<(string ToolName, IReadOnlyDictionary<string, object?> Arguments, ToolResultType ResultType)>
        _toolResults = [];

    public event Action<string>? InputReceived;
    public event Action? ShutdownRequested;

    public bool IsStarted { get; private set; }
    public bool IsStopped { get; private set; }
    public bool IsCleared { get; private set; }
    public IReadOnlyList<ChatLine[]> DisplayedMessages => _displayedMessages;
    public IReadOnlyList<string> SystemMessages => _systemMessages;

    public IReadOnlyList<(string ToolName, IReadOnlyDictionary<string, object?> Arguments, ToolResultType ResultType)>
        ToolResults =>
        _toolResults;

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

    public void ShowToolResult(string toolName, IReadOnlyDictionary<string, object?> arguments,
        ToolResultType resultType)
    {
        _toolResults.Add((toolName, arguments, resultType));
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