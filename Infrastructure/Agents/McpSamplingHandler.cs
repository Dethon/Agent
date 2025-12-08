using System.Collections.Concurrent;
using Infrastructure.Agents.Mappers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents;

internal sealed class McpSamplingHandler(
    Func<AgentThread> getNewThread,
    Func<string?, ChatClientAgent> createInnerAgent,
    Func<CreateMessageRequestParams?, ChatClientAgentRunOptions> getRunOptions)
{
    private readonly ConcurrentDictionary<string, AgentThread> _coAgentConversations = [];

    public Func<
            CreateMessageRequestParams?,
            IProgress<ProgressNotificationValue>,
            CancellationToken,
            ValueTask<CreateMessageResult>>
        CreateHandler(Func<bool> isDisposed)
    {
        return async (parameters, progress, ct) =>
        {
            ObjectDisposedException.ThrowIf(isDisposed(), this);

            var thread = GetOrCreateThread(parameters);
            var coAgent = createInnerAgent(parameters?.SystemPrompt);
            var messages = MapMessages(parameters);
            var options = ConfigureOptions(parameters);

            return await RunAndCollectUpdates(coAgent, messages, thread, options, progress, ct);
        };
    }

    private AgentThread GetOrCreateThread(CreateMessageRequestParams? parameters)
    {
        var tracker = parameters?.Metadata?.GetProperty("tracker").GetString();
        return tracker is null
            ? getNewThread()
            : _coAgentConversations.GetOrAdd(tracker, _ => getNewThread());
    }

    private static ChatMessage[] MapMessages(CreateMessageRequestParams? parameters)
    {
        return parameters?.Messages
            .Select(x => new ChatMessage(
                x.Role == Role.Assistant ? ChatRole.Assistant : ChatRole.User,
                x.Content.ToAIContents()))
            .ToArray() ?? [];
    }

    private ChatClientAgentRunOptions ConfigureOptions(CreateMessageRequestParams? parameters)
    {
        var options = getRunOptions(parameters);
        var includeContext = parameters?.IncludeContext ?? ContextInclusion.None;

        if (includeContext == ContextInclusion.None && options.ChatOptions is not null)
            options.ChatOptions.Tools = [];

        return options;
    }

    private static async Task<CreateMessageResult> RunAndCollectUpdates(
        ChatClientAgent agent,
        ChatMessage[] messages,
        AgentThread thread,
        ChatClientAgentRunOptions options,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken ct)
    {
        List<AgentRunResponseUpdate> updates = [];
        await foreach (var update in agent.RunStreamingAsync(messages, thread, options, ct))
        {
            updates.Add(update);
            progress.Report(new ProgressNotificationValue { Progress = updates.Count });
        }

        return updates.ToCreateMessageResult();
    }
}