using System.Runtime.CompilerServices;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.CliGui.Abstractions;

namespace Infrastructure.Clients;

public sealed class CliChatMessengerClient : IChatMessengerClient, IDisposable
{
    private readonly ICliChatMessageRouter _router;
    private readonly IThreadStateStore? _threadStateStore;
    private bool _historyRestored;

    public CliChatMessengerClient(
        ICliChatMessageRouter router,
        Action? onShutdownRequested = null,
        IThreadStateStore? threadStateStore = null)
    {
        _router = router;
        _threadStateStore = threadStateStore;

        if (onShutdownRequested is not null)
        {
            _router.ShutdownRequested += onShutdownRequested;
        }
    }

    public async IAsyncEnumerable<ChatPrompt> ReadPrompts(
        int timeout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await RestoreHistoryOnceAsync();
        var enumerable = _router.ReadPrompts(cancellationToken).ToAsyncEnumerable();
        await foreach (var prompt in enumerable.WithCancellation(cancellationToken))
        {
            yield return prompt;
        }
    }

    public Task SendResponse(
        long chatId, ChatResponseMessage responseMessage, long? threadId, string? botTokenHash,
        CancellationToken cancellationToken)
    {
        _router.SendResponse(responseMessage);
        return Task.CompletedTask;
    }

    public Task<int> CreateThread(long chatId, string name, string? botTokenHash, CancellationToken cancellationToken)
    {
        _router.CreateThread(name);
        return Task.FromResult(_router.ThreadId);
    }

    public Task<bool> DoesThreadExist(long chatId, long threadId, string? botTokenHash,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
    }

    public void Dispose()
    {
        _router.Dispose();
    }

    private async Task RestoreHistoryOnceAsync()
    {
        if (_historyRestored || _threadStateStore is null)
        {
            return;
        }

        _historyRestored = true;

        var agentKey = new AgentKey(_router.ChatId, _router.ThreadId);
        var history = await _threadStateStore.GetMessagesAsync(agentKey.ToString());
        if (history is not { Length: > 0 })
        {
            return;
        }

        _router.RestoreHistory(history);
    }
}