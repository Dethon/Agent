using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Text;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Clients.Cli;

namespace Infrastructure.Clients;

public class CliChatMessengerClient : IChatMessengerClient, IDisposable
{
    private readonly long _chatId;
    private readonly int _threadId;
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
        (_chatId, _threadId) = DeriveIdsFromAgentName(agentName);
        _router = new CliChatMessageRouter(agentName, userName, terminalAdapter, _chatId, _threadId);

        if (onShutdownRequested is not null)
        {
            _router.ShutdownRequested += onShutdownRequested;
        }
    }

    public async IAsyncEnumerable<ChatPrompt> ReadPrompts(
        int timeout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        RestoreHistoryOnceAsync();
        var enumerable = _router.ReadPrompts(cancellationToken).ToAsyncEnumerable();
        await foreach (var prompt in enumerable.WithCancellation(cancellationToken))
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
        return Task.FromResult(_threadId);
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

    private void RestoreHistoryOnceAsync()
    {
        if (_historyRestored || _threadStateStore is null)
        {
            return;
        }

        _historyRestored = true;

        var agentKey = new AgentKey(_chatId, _threadId);
        var history = _threadStateStore.GetMessages(agentKey.ToString());
        if (history is not { Length: > 0 })
        {
            return;
        }

        var lines = ChatHistoryMapper.MapToDisplayLines(history, _agentName, _userName).ToArray();
        if (lines.Length == 0)
        {
            return;
        }

        _terminalAdapter.ShowSystemMessage("--- Previous conversation restored ---");
        _terminalAdapter.DisplayMessage(lines);
    }

    private static (long ChatId, int ThreadId) DeriveIdsFromAgentName(string agentName)
    {
        var bytes = Encoding.UTF8.GetBytes(agentName.ToLowerInvariant());
        var hash = XxHash32.HashToUInt32(bytes);
        var threadId = (int)(hash & 0x7FFFFFFF);
        return (hash, threadId);
    }
}