using System.Threading.Channels;
using Domain.Extensions;
using JetBrains.Annotations;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Domain.Agents;

[PublicAPI]
public abstract class CancellableAiAgent : AIAgent, IAsyncDisposable
{
    public abstract void CancelCurrentExecution();
    public abstract ValueTask DisposeAsync();
    public abstract ChannelReader<AgentRunResponseUpdate> Subscribe(bool switchSubscription);

    public virtual IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingWithNotificationsAsync(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        bool switchSubscription = true,
        CancellationToken cancellationToken = default)
    {
        var channelReader = Subscribe(switchSubscription);
        var mainResponses = RunStreamingAsync(messages, thread, options, cancellationToken);
        var notificationResponses = channelReader.ReadAllAsync(cancellationToken);
        return mainResponses.Merge(notificationResponses, cancellationToken);
    }

    public IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingWithNotificationsAsync(
        string message,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        bool switchSubscription = true,
        CancellationToken cancellationToken = default)
    {
        return RunStreamingWithNotificationsAsync(
            [new ChatMessage(ChatRole.User, message)], thread, options, switchSubscription, cancellationToken);
    }

    public IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingWithNotificationsAsync(
        ChatMessage message,
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        bool switchSubscription = true,
        CancellationToken cancellationToken = default)
    {
        return RunStreamingWithNotificationsAsync([message], thread, options, switchSubscription, cancellationToken);
    }

    public IAsyncEnumerable<AgentRunResponseUpdate> RunStreamingWithNotificationsAsync(
        AgentThread? thread = null,
        AgentRunOptions? options = null,
        bool switchSubscription = true,
        CancellationToken cancellationToken = default)
    {
        return RunStreamingWithNotificationsAsync([], thread, options, switchSubscription, cancellationToken);
    }
}