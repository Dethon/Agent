using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Domain.DTOs;

namespace Infrastructure.Clients.Cli;

internal sealed class CliChatMessageRouter : IDisposable
{
    private const long DefaultChatId = 1;
    private const int DefaultThreadId = 1;

    private readonly string _agentName;
    private readonly string _userName;
    private readonly ITerminalAdapter _terminalAdapter;
    private readonly CliCommandHandler _commandHandler;
    private readonly CliToolApprovalHandler? _approvalHandler;

    private BlockingCollection<string> _inputQueue = new();
    private int _messageCounter;
    private bool _isRunning;
    private bool _isStarted;

    public CliChatMessageRouter(
        string agentName,
        string userName,
        ITerminalAdapter terminalAdapter,
        CliToolApprovalHandler? approvalHandler = null)
    {
        _agentName = agentName;
        _userName = userName;
        _terminalAdapter = terminalAdapter;
        _approvalHandler = approvalHandler;
        _commandHandler = new CliCommandHandler(ClearHistory, AddToHistory);

        _terminalAdapter.InputReceived += OnInputReceived;
        _terminalAdapter.ShutdownRequested += OnShutdownRequested;
    }

    public event Action? ShutdownRequested;

    public async IAsyncEnumerable<ChatPrompt> ReadPrompts(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!_isStarted)
        {
            _terminalAdapter.Start();
            _isStarted = true;
            _isRunning = true;
        }

        cancellationToken.Register(() =>
        {
            _isRunning = false;
            _terminalAdapter.Stop();
        });

        while (!cancellationToken.IsCancellationRequested && _isRunning)
        {
            string? input;
            try
            {
                if (_inputQueue.IsCompleted)
                {
                    yield break;
                }

                input = _inputQueue.Take(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                yield break;
            }

            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            AddToHistory(_userName, input, isUser: true);

            yield return new ChatPrompt
            {
                Prompt = input,
                ChatId = DefaultChatId,
                MessageId = Interlocked.Increment(ref _messageCounter),
                Sender = _userName,
                ThreadId = DefaultThreadId
            };

            await Task.CompletedTask;
        }
    }

    public void SendResponse(ChatResponseMessage responseMessage)
    {
        if (!string.IsNullOrWhiteSpace(responseMessage.CalledTools))
        {
            AddToHistory("[Tools]", responseMessage.CalledTools, isUser: false, isToolCall: true);
        }

        if (!string.IsNullOrWhiteSpace(responseMessage.Message))
        {
            AddToHistory(_agentName, responseMessage.Message, isUser: false);
        }
    }

    public void CreateThread(string name)
    {
        AddToHistory("[System]", $"--- {name} ---", isUser: false, isSystem: true);
    }

    public void Dispose()
    {
        _isRunning = false;
        _terminalAdapter.InputReceived -= OnInputReceived;
        _terminalAdapter.ShutdownRequested -= OnShutdownRequested;
        _inputQueue.Dispose();
    }

    private void OnInputReceived(string input)
    {
        // Check for approval input first
        if (_approvalHandler is not null && _approvalHandler.TryHandleApprovalInput(input))
        {
            return;
        }

        if (!_commandHandler.TryHandleCommand(input))
        {
            _inputQueue.Add(input);
        }
    }

    private void OnShutdownRequested()
    {
        _isRunning = false;
        _inputQueue.CompleteAdding();
        ShutdownRequested?.Invoke();
    }

    private void AddToHistory(string sender, string message, bool isUser, bool isToolCall = false,
        bool isSystem = false)
    {
        var chatMessage = new ChatMessage(sender, message, isUser, isToolCall, isSystem, DateTime.Now);
        var lines = ChatMessageFormatter.FormatMessage(chatMessage).ToArray();
        _terminalAdapter.DisplayMessage(lines);
    }

    private void ClearHistory()
    {
        _inputQueue.Add("/cancel");
        var oldQueue = _inputQueue;
        _inputQueue = new BlockingCollection<string>();
        oldQueue.CompleteAdding();
        _terminalAdapter.ClearDisplay();
    }
}