using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Text;
using Domain.DTOs;
using Infrastructure.CliGui.Abstractions;
using Infrastructure.CliGui.Rendering;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Infrastructure.CliGui.Routing;

public sealed class CliChatMessageRouter : ICliChatMessageRouter
{
    public long ChatId { get; }
    public int ThreadId { get; }

    private readonly string _agentName;
    private readonly string _userName;
    private readonly ITerminalSession _terminalAdapter;
    private readonly CliCommandHandler _commandHandler;

    private BlockingCollection<string> _inputQueue = new();
    private int _messageCounter;
    private bool _isRunning;
    private bool _isStarted;

    public CliChatMessageRouter(
        string agentName,
        string userName,
        ITerminalSession terminalAdapter)
    {
        var (chatId, threadId) = DeriveIdsFromAgentName(agentName);

        _agentName = agentName;
        _userName = userName;
        _terminalAdapter = terminalAdapter;
        ChatId = chatId;
        ThreadId = threadId;
        _commandHandler = new CliCommandHandler(terminalAdapter, ResetInputQueue);

        _terminalAdapter.InputReceived += OnInputReceived;
        _terminalAdapter.ShutdownRequested += OnShutdownRequested;
    }

    public event Action? ShutdownRequested;

    public void RestoreHistory(IReadOnlyList<AiChatMessage> messages)
    {
        var lines = ChatHistoryMapper.MapToDisplayLines(messages, _agentName, _userName).ToArray();
        if (lines.Length == 0)
        {
            return;
        }

        _terminalAdapter.ShowSystemMessage("--- Previous conversation restored ---");
        _terminalAdapter.DisplayMessage(lines);
    }

    public IEnumerable<ChatPrompt> ReadPrompts(CancellationToken cancellationToken)
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
                ChatId = ChatId,
                MessageId = Interlocked.Increment(ref _messageCounter),
                Sender = _userName,
                ThreadId = ThreadId
            };
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
        _terminalAdapter.Dispose();
    }

    private static (long ChatId, int ThreadId) DeriveIdsFromAgentName(string agentName)
    {
        var bytes = Encoding.UTF8.GetBytes(agentName.ToLowerInvariant());
        var hash = XxHash32.HashToUInt32(bytes);
        var threadId = (int)(hash & 0x7FFFFFFF);
        return (hash, threadId);
    }

    private void OnInputReceived(string input)
    {
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

    private void ResetInputQueue(bool wipeThread)
    {
        var command = wipeThread ? "/clear" : "/cancel";
        _inputQueue.Add(command);
        var oldQueue = _inputQueue;
        _inputQueue = new BlockingCollection<string>();
        oldQueue.CompleteAdding();
    }
}