using System.Collections.Concurrent;
using Infrastructure.Agents.Mappers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents;

internal sealed class McpSamplingHandler(
    ChatClientAgent agent,
    Func<IReadOnlyList<AITool>> toolsProvider) : IDisposable
{
    private readonly ConcurrentDictionary<string, AgentThread> _trackedConversations = [];
    private bool _isDisposed;

    public void Dispose()
    {
        _isDisposed = true;
    }

    public async ValueTask<CreateMessageResult> HandleAsync(
        CreateMessageRequestParams? parameters,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        var thread = GetOrCreateThread(parameters);
        var messages = MapMessages(parameters);
        var options = CreateOptions(parameters);

        return await ExecuteAndReport(messages, thread, options, progress, ct);
    }

    private AgentThread GetOrCreateThread(CreateMessageRequestParams? parameters)
    {
        var tracker = parameters?.Metadata?.GetProperty("tracker").GetString();
        return tracker is null
            ? agent.GetNewThread()
            : _trackedConversations.GetOrAdd(tracker, static (_, a) => a.GetNewThread(), agent);
    }

    private static ChatMessage[] MapMessages(CreateMessageRequestParams? parameters)
    {
        return parameters?.Messages
            .Select(m => new ChatMessage(
                m.Role == Role.Assistant ? ChatRole.Assistant : ChatRole.User,
                m.Content.ToAIContents()))
            .ToArray() ?? [];
    }

    private ChatClientAgentRunOptions CreateOptions(CreateMessageRequestParams? parameters)
    {
        IList<AITool> tools = (parameters?.IncludeContext ?? ContextInclusion.None) == ContextInclusion.None
            ? []
            : [..toolsProvider()];

        var chatOptions = new ChatOptions
        {
            Tools = tools,
            Instructions = parameters?.SystemPrompt,
            Temperature = parameters?.Temperature,
            MaxOutputTokens = parameters?.MaxTokens,
            StopSequences = parameters?.StopSequences?.ToArray()
        };

        return new ChatClientAgentRunOptions(chatOptions);
    }

    private async Task<CreateMessageResult> ExecuteAndReport(
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