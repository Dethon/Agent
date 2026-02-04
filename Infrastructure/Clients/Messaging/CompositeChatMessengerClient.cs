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
    public MessageSource Source => MessageSource.WebUi;

    public bool SupportsScheduledNotifications => clients.Any(c => c.SupportsScheduledNotifications);

    public async IAsyncEnumerable<ChatPrompt> ReadPrompts(
        int timeout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Validate();
        var merged = clients
            .Select(c => c.ReadPrompts(timeout, cancellationToken))
            .Merge(cancellationToken);

        await foreach (var prompt in merged)
        {
            yield return prompt;
        }
    }

    public async Task ProcessResponseStreamAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
        CancellationToken cancellationToken)
    {
        Validate();
        var channels = clients
            .Select(_ => Channel.CreateUnbounded<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>())
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
        MessageSource source,
        long? chatId,
        long? threadId,
        string? agentId,
        string? topicName,
        CancellationToken ct = default)
    {
        Validate();
        var client = clients.FirstOrDefault(c => c.Source == source);
        return client != null
            ? await client.CreateTopicIfNeededAsync(source, chatId, threadId, agentId, topicName, ct)
            : new AgentKey(chatId ?? 0, threadId ?? 0, agentId ?? string.Empty);
    }

    public async Task StartScheduledStreamAsync(AgentKey agentKey, MessageSource source, CancellationToken ct = default)
    {
        Validate();
        var client = clients.FirstOrDefault(c => c.Source == source);
        if (client != null)
        {
            await client.StartScheduledStreamAsync(agentKey, source, ct);
        }
    }

    private void Validate()
    {
        if (clients.Count == 0)
        {
            throw new InvalidOperationException($"{nameof(clients)} must contain at least one client");
        }
    }

    private async Task BroadcastUpdatesAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> source,
        (IChatMessengerClient client, Channel<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> channel)[]
            clientChannelPairs,
        CancellationToken ct)
    {
        try
        {
            await foreach (var update in source.WithCancellation(ct))
            {
                var (_, _, _, messageSource) = update;

                var writeTasks = clientChannelPairs
                    .Where(pair =>
                        pair.client.Source == MessageSource.WebUi || // WebUi always receives
                        pair.client.Source == messageSource) // Source matches
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