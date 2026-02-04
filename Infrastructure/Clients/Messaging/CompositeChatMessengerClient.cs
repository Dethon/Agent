using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Agents.AI;

namespace Infrastructure.Clients.Messaging;

public sealed class CompositeChatMessengerClient(
    IReadOnlyList<IChatMessengerClient> clients) : IChatMessengerClient
{
    private readonly ConcurrentDictionary<long, MessageSource> _chatIdToSource = new();

    public MessageSource Source => MessageSource.WebUi;

    public bool SupportsScheduledNotifications => clients.Any(c => c.SupportsScheduledNotifications);

    public async IAsyncEnumerable<ChatPrompt> ReadPrompts(int timeout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Validate();
        var merged = clients
            .Select(c => c.ReadPrompts(timeout, cancellationToken))
            .Merge(cancellationToken);

        await foreach (var prompt in merged.WithCancellation(cancellationToken))
        {
            _chatIdToSource[prompt.ChatId] = prompt.Source;
            yield return prompt;
        }
    }

    public async Task ProcessResponseStreamAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates,
        CancellationToken cancellationToken)
    {
        Validate();
        var channels = clients
            .Select(_ => Channel.CreateUnbounded<(AgentKey, AgentResponseUpdate, AiResponse?)>())
            .ToArray();

        var clientChannelPairs = clients
            .Zip(channels, (client, channel) => (client, channel))
            .ToArray();

        var broadcastTask = BroadcastUpdatesAsync(updates, clientChannelPairs, cancellationToken);

        var processTasks = clientChannelPairs
            .Select(pair => pair.client.ProcessResponseStreamAsync(
                pair.channel.Reader.ReadAllAsync(cancellationToken),
                cancellationToken))
            .ToArray();

        await broadcastTask;
        await Task.WhenAll(processTasks);
    }

    public async Task<bool> DoesThreadExist(long chatId, long threadId, string? agentId,
        CancellationToken cancellationToken)
    {
        Validate();
        var existsTasks = clients
            .Select(client => client.DoesThreadExist(chatId, threadId, agentId, cancellationToken));
        var results = await Task.WhenAll(existsTasks);
        return results.Any(exists => exists);
    }

    public async Task<AgentKey> CreateTopicIfNeededAsync(
        long? chatId,
        long? threadId,
        string? agentId,
        string? topicName,
        CancellationToken ct = default)
    {
        Validate();
        var agentKeys = clients.Select(x => x.CreateTopicIfNeededAsync(chatId, threadId, agentId, topicName, ct));
        return (await Task.WhenAll(agentKeys)).First();
    }

    public async Task StartScheduledStreamAsync(AgentKey agentKey, CancellationToken ct = default)
    {
        Validate();
        await Task.WhenAll(clients.Select(c => c.StartScheduledStreamAsync(agentKey, ct)));
    }

    private void Validate()
    {
        if (clients.Count == 0)
        {
            throw new InvalidOperationException($"{nameof(clients)} must contain at least one client"); 
        }
    }

    private async Task BroadcastUpdatesAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> source,
        (IChatMessengerClient client, Channel<(AgentKey, AgentResponseUpdate, AiResponse?)> channel)[] clientChannelPairs,
        CancellationToken ct)
    {
        try
        {
            await foreach (var update in source.WithCancellation(ct))
            {
                var (agentKey, _, _) = update;
                var isKnownChatId = _chatIdToSource.TryGetValue(agentKey.ChatId, out var promptSource);

                var writeTasks = clientChannelPairs
                    .Where(pair =>
                        pair.client.Source == MessageSource.WebUi ||  // WebUi always receives
                        !isKnownChatId ||                             // Unknown ChatId broadcasts to all (fail-safe)
                        pair.client.Source == promptSource)           // Source matches
                    .Select(pair => pair.channel.Writer.WriteAsync(update, ct).AsTask());

                await Task.WhenAll(writeTasks);
            }
        }
        finally
        {
            foreach (var (_, channel) in clientChannelPairs)
            {
                channel.Writer.TryComplete();
            }
        }
    }
}