using System.Collections.Concurrent;
using Infrastructure.Agents.Mappers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents.Mcp;

internal sealed class McpSamplingHandler
{
    private readonly ChatClientAgent _agent;
    private readonly Func<IReadOnlyList<AITool>> _toolsProvider;
    private readonly ConcurrentDictionary<string, AgentSession> _trackedConversations = [];

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
        var thread = await GetOrCreateThread(parameters, ct);
        var messages = MapMessages(parameters);
        var options = CreateOptions(parameters);

        return await ExecuteAndReport(messages, thread, options, progress, ct);
    }

    private async ValueTask<AgentSession> GetOrCreateThread(CreateMessageRequestParams? parameters,
        CancellationToken ct)
    {
        var tracker = parameters?.Metadata?.GetProperty("tracker").GetString();
        var thread = await _agent.GetNewSessionAsync(ct);
        return tracker is null ? thread : _trackedConversations.GetOrAdd(tracker, thread);
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
        AgentSession thread,
        ChatClientAgentRunOptions options,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken ct)
    {
        List<AgentResponseUpdate> updates = [];
        await foreach (var update in _agent.RunStreamingAsync(messages, thread, options, ct))
        {
            updates.Add(update);
            progress.Report(new ProgressNotificationValue { Progress = updates.Count });
        }

        return updates.ToCreateMessageResult();
    }
}