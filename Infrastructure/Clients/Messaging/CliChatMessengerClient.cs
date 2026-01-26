using System.Runtime.CompilerServices;
using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.CliGui.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Infrastructure.Clients.Messaging;

public sealed class CliChatMessengerClient : IChatMessengerClient, IDisposable
{
    private readonly ICliChatMessageRouter _router;
    private readonly IThreadStateStore? _threadStateStore;
    private bool _historyRestored;

    public bool SupportsScheduledNotifications => false;

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

    public async Task ProcessResponseStreamAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates,
        CancellationToken cancellationToken)
    {
        string? currentMessageId = null;
        var messageIndex = 0;

        await foreach (var (_, update, _) in updates.WithCancellation(cancellationToken))
        {
            if (update.MessageId is not null && update.MessageId != currentMessageId)
            {
                if (currentMessageId is not null)
                {
                    messageIndex++;
                }

                currentMessageId = update.MessageId;
            }

            foreach (var content in update.Contents)
            {
                var msg = content switch
                {
                    TextContent tc => new ChatResponseMessage { Message = tc.Text, MessageIndex = messageIndex },
                    TextReasoningContent rc => new ChatResponseMessage
                    {
                        Reasoning = rc.Text, MessageIndex = messageIndex
                    },
                    FunctionCallContent fc => new ChatResponseMessage
                    {
                        CalledTools = $"{fc.Name}({JsonSerializer.Serialize(fc.Arguments)})",
                        MessageIndex = messageIndex
                    },
                    _ => null
                };

                if (msg is not null)
                {
                    _router.SendResponse(msg);
                }
            }
        }

        _router.SendResponse(new ChatResponseMessage { IsComplete = true, MessageIndex = messageIndex });
    }

    public Task<int> CreateThread(long chatId, string name, string? agentId, CancellationToken cancellationToken)
    {
        _router.CreateThread(name);
        return Task.FromResult(_router.ThreadId);
    }

    public Task<bool> DoesThreadExist(long chatId, long threadId, string? agentId,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
    }

    public Task<AgentKey> CreateTopicIfNeededAsync(
        long? chatId,
        long? threadId,
        string? agentId,
        string? topicName,
        CancellationToken ct = default)
    {
        return Task.FromResult(new AgentKey(chatId ?? 0, threadId ?? 0, agentId));
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