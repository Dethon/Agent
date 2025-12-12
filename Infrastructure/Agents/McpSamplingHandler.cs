using System.Collections.Concurrent;
using Infrastructure.Agents.Mappers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents;

internal sealed class McpSamplingHandler
{
    private readonly ChatClientAgent _agent;
    private readonly Func<IReadOnlyList<AITool>> _toolsProvider;
    private readonly ConcurrentDictionary<string, AgentThread> _trackedConversations = [];

    public McpSamplingHandler(ChatClientAgent agent, Func<IReadOnlyList<AITool>> toolsProvider)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(toolsProvider);

        _agent = agent;
        _toolsProvider = toolsProvider;
    }

    public async ValueTask<CreateMessageResult> HandleAsync(
        CreateMessageRequestParams? parameters,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken ct)
    {
        var thread = GetOrCreateThread(parameters);
        var messages = MapMessages(parameters);
        var options = CreateOptions(parameters);

        return await ExecuteAndReport(messages, thread, options, progress, ct);
    }

    private AgentThread GetOrCreateThread(CreateMessageRequestParams? parameters)
    {
        var tracker = parameters?.Metadata?.GetProperty("tracker").GetString();
        return tracker is null
            ? _agent.GetNewThread()
            : _trackedConversations.GetOrAdd(tracker, static (_, a) => a.GetNewThread(), _agent);
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
        var includeTools = (parameters?.IncludeContext ?? ContextInclusion.None) != ContextInclusion.None;
        var tools = includeTools ? _toolsProvider() : [];

        return new ChatClientAgentRunOptions(new ChatOptions
        {
            Tools = [..tools],
            Instructions = parameters?.SystemPrompt,
            Temperature = parameters?.Temperature,
            MaxOutputTokens = parameters?.MaxTokens,
            StopSequences = parameters?.StopSequences?.ToArray()
        });
    }

    private async Task<CreateMessageResult> ExecuteAndReport(
        ChatMessage[] messages,
        AgentThread thread,
        ChatClientAgentRunOptions options,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken ct)
    {
        List<AgentRunResponseUpdate> updates = [];
        await foreach (var update in _agent.RunStreamingAsync(messages, thread, options, ct))
        {
            updates.Add(update);
            progress.Report(new ProgressNotificationValue { Progress = updates.Count });
        }

        return updates.ToCreateMessageResult();
    }
}