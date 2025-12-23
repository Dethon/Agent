using System.Runtime.CompilerServices;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Clients.Cli;

namespace Infrastructure.Clients;

public class CliChatMessengerClient : IChatMessengerClient, IDisposable
{
    private const long DefaultChatId = 1;
    private const int DefaultThreadId = 1;

    private readonly string _agentName;
    private readonly string _userName;
    private readonly CliChatMessageRouter _router;
    private readonly ITerminalAdapter _terminalAdapter;
    private readonly IThreadStateStore? _threadStateStore;
    private bool _historyRestored;

    public CliChatMessengerClient(
        string agentName,
        string userName,
        ITerminalAdapter terminalAdapter,
        Action? onShutdownRequested = null,
        IThreadStateStore? threadStateStore = null)
    {
        _agentName = agentName;
        _userName = userName;
        _terminalAdapter = terminalAdapter;
        _threadStateStore = threadStateStore;
        _router = new CliChatMessageRouter(agentName, userName, terminalAdapter);

        if (onShutdownRequested is not null)
        {
            _router.ShutdownRequested += onShutdownRequested;
        }
    }

    public async IAsyncEnumerable<ChatPrompt> ReadPrompts(
        int timeout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await RestoreHistoryOnceAsync();

        await foreach (var prompt in _router.ReadPrompts(cancellationToken).ToAsyncEnumerable())
        {
            yield return prompt;
        }
    }

    public Task SendResponse(
        long chatId, ChatResponseMessage responseMessage, long? threadId, CancellationToken cancellationToken)
    {
        _router.SendResponse(responseMessage);
        return Task.CompletedTask;
    }

    public Task<int> CreateThread(long chatId, string name, CancellationToken cancellationToken)
    {
        _router.CreateThread(name);
        return Task.FromResult(DefaultThreadId);
    }

    public Task<bool> DoesThreadExist(long chatId, long threadId, CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
    }

    public void Dispose()
    {
        _router.Dispose();
        _terminalAdapter.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task RestoreHistoryOnceAsync()
    {
        if (_historyRestored || _threadStateStore is null)
        {
            return;
        }

        _historyRestored = true;

        var agentKey = new AgentKey(DefaultChatId, DefaultThreadId);
        var history = await _threadStateStore.GetMessagesAsync(agentKey.ToString());
        if (history is not { Length: > 0 })
        {
            return;
        }

        var lines = ChatHistoryMapper.MapToDisplayLines(history, _agentName, _userName).ToArray();
        if (lines.Length > 0)
        {
            _terminalAdapter.ShowSystemMessage("--- Previous conversation restored ---");
            _terminalAdapter.DisplayMessage(lines);
        }
    }
}