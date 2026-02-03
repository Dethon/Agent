using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging;

public sealed class ServiceBusChatMessengerClient(
    ServiceBusSourceMapper sourceMapper,
    ServiceBusResponseWriter responseWriter,
    ILogger<ServiceBusChatMessengerClient> logger,
    string defaultAgentId) : IChatMessengerClient
{
    private readonly Channel<ChatPrompt> _promptChannel = Channel.CreateUnbounded<ChatPrompt>();
    private readonly ConcurrentDictionary<long, string> _chatIdToSourceId = new();
    private readonly ConcurrentDictionary<long, StringBuilder> _responseAccumulators = new();
    private int _messageIdCounter;

    public bool SupportsScheduledNotifications => false;

    public async IAsyncEnumerable<ChatPrompt> ReadPrompts(
        int timeout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var prompt in _promptChannel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return prompt;
        }
    }

    public async Task ProcessResponseStreamAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates,
        CancellationToken cancellationToken)
    {
        await foreach (var (key, update, aiResponse) in updates.WithCancellation(cancellationToken))
        {
            if (!_chatIdToSourceId.TryGetValue(key.ChatId, out var sourceId))
            {
                continue;
            }

            var accumulator = _responseAccumulators.GetOrAdd(key.ChatId, _ => new StringBuilder());

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                        accumulator.Append(tc.Text);
                        break;

                    case StreamCompleteContent:
                        if (accumulator.Length > 0)
                        {
                            await responseWriter.WriteResponseAsync(
                                sourceId,
                                key.AgentId ?? defaultAgentId,
                                accumulator.ToString(),
                                cancellationToken);

                            accumulator.Clear();
                        }
                        _responseAccumulators.TryRemove(key.ChatId, out _);
                        break;
                }
            }
        }
    }

    public Task<int> CreateThread(long chatId, string name, string? agentId, CancellationToken cancellationToken)
    {
        return Task.FromResult(0);
    }

    public Task<bool> DoesThreadExist(long chatId, long threadId, string? agentId, CancellationToken cancellationToken)
    {
        return Task.FromResult(false);
    }

    public Task<AgentKey> CreateTopicIfNeededAsync(
        long? chatId,
        long? threadId,
        string? agentId,
        string? topicName,
        CancellationToken ct = default)
    {
        return Task.FromResult(new AgentKey(chatId ?? 0, threadId ?? 0, agentId ?? defaultAgentId));
    }

    public Task StartScheduledStreamAsync(AgentKey agentKey, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public async Task EnqueueReceivedMessageAsync(
        string prompt,
        string sender,
        string sourceId,
        string? agentId,
        CancellationToken ct = default)
    {
        var actualAgentId = string.IsNullOrEmpty(agentId) ? defaultAgentId : agentId;

        var (chatId, threadId, _, _) = await sourceMapper.GetOrCreateMappingAsync(sourceId, actualAgentId, ct);

        _chatIdToSourceId[chatId] = sourceId;

        var messageId = Interlocked.Increment(ref _messageIdCounter);

        var chatPrompt = new ChatPrompt
        {
            Prompt = prompt,
            ChatId = chatId,
            ThreadId = (int)threadId,
            MessageId = messageId,
            Sender = sender,
            AgentId = actualAgentId
        };

        logger.LogInformation(
            "Enqueued prompt from Service Bus: sourceId={SourceId}, chatId={ChatId}, threadId={ThreadId}",
            sourceId, chatId, threadId);

        await _promptChannel.Writer.WriteAsync(chatPrompt, ct);
    }
}
