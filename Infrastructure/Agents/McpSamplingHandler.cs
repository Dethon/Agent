using System.Collections.Concurrent;
using Infrastructure.Agents.Mappers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents;

internal sealed class McpSamplingHandler(IChatClient chatClient, Func<IReadOnlyList<AITool>> toolsProvider)
    : IDisposable
{
    private readonly ConcurrentDictionary<string, AgentThread> _coAgentConversations = [];
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

        var coAgent = CreateInnerAgent(parameters?.SystemPrompt);
        var thread = GetOrCreateThread(parameters, coAgent);
        var messages = MapMessages(parameters);
        var options = ConfigureOptions(parameters);

        return await RunAndCollectUpdates(coAgent, messages, thread, options, progress, ct);
    }

    private AgentThread GetOrCreateThread(CreateMessageRequestParams? parameters, ChatClientAgent agent)
    {
        var tracker = parameters?.Metadata?.GetProperty("tracker").GetString();
        return tracker is null
            ? agent.GetNewThread()
            : _coAgentConversations.GetOrAdd(tracker, static (_, a) => a.GetNewThread(), agent);
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
        var options = CreateRunOptions(parameters);
        var includeContext = parameters?.IncludeContext ?? ContextInclusion.None;

        if (includeContext == ContextInclusion.None && options.ChatOptions is not null)
        {
            options.ChatOptions.Tools = [];
        }

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

    private ChatClientAgent CreateInnerAgent(string? systemPrompt)
    {
        var chatOptions = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["reasoning_effort"] = "low" },
            Instructions = systemPrompt
        };

        return chatClient.CreateAIAgent(new ChatClientAgentOptions
        {
            Name = "jack-sampling",
            ChatOptions = chatOptions
        });
    }

    private ChatClientAgentRunOptions CreateRunOptions(CreateMessageRequestParams? parameters = null)
    {
        var chatOptions = new ChatOptions
        {
            Tools = [..toolsProvider()],
            AdditionalProperties = new AdditionalPropertiesDictionary { ["reasoning_effort"] = "low" }
        };

        if (parameters is null)
        {
            return new ChatClientAgentRunOptions(chatOptions);
        }

        chatOptions.Temperature = parameters.Temperature;
        chatOptions.MaxOutputTokens = parameters.MaxTokens;
        chatOptions.StopSequences = parameters.StopSequences?.ToArray();

        return new ChatClientAgentRunOptions(chatOptions);
    }
}