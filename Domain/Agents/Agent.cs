using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace Domain.Agents;

public class Agent(
    Message[] messages,
    ILargeLanguageModel llm,
    ITool[] tools,
    int maxDepth,
    ILogger<Agent> logger) : IAgent
{
    private readonly ConcurrentDictionary<CancellationToken, CancellationTokenSource> _cancelTokenSources = [];
    private readonly Lock _messagesLock = new();

    private readonly Dictionary<string, ITool> _tools = tools.ToDictionary(x => x.GetToolDefinition().Name, x => x);
    private readonly ToolDefinition[] _toolDefs = tools.Select(x => x.GetToolDefinition()).ToArray();

    private readonly List<Message> _messages = messages.ToList();

    public IAsyncEnumerable<AgentResponse> Run(
        string? prompt, bool cancelCurrentOperation, CancellationToken cancellationToken = default)
    {
        if (prompt is null)
        {
            return Run([], cancelCurrentOperation, cancellationToken);
        }

        var message = new Message
        {
            Role = Role.User,
            Content = prompt
        };
        return Run([message], cancelCurrentOperation, cancellationToken);
    }

    public IAsyncEnumerable<AgentResponse> Run(
        Message[] prompts, bool cancelCurrentOperation, CancellationToken cancellationToken = default)
    {
        UpdateConversation(prompts);
        var childCancellationToken = GetChildCancellationToken(cancelCurrentOperation, cancellationToken);
        return ExecuteAgentLoop(0.5f, childCancellationToken);
    }

    private async IAsyncEnumerable<AgentResponse> ExecuteAgentLoop(
        float? temperature = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<Task<Message?>> longRunningTasks = [];
        for (var i = 0; i < maxDepth && !cancellationToken.IsCancellationRequested; i++)
        {
            var responses = await llm.Prompt(GetConversationSnapshot(), _toolDefs, temperature, cancellationToken);
            foreach (var responseMessage in responses)
            {
                yield return responseMessage;
            }

            var toolResponses = await ProcessToolCalls(responses, cancellationToken);
            UpdateConversation([..responses, ..toolResponses]);
            longRunningTasks.AddRange(toolResponses
                .Select(x => x.LongRunningTask)
                .Where(x => x is not null)
                .Cast<Task<Message?>>());
            
            if (toolResponses.Length == 0 && !(await TryProcessLongRunningTasks(longRunningTasks)))
            {
                yield break;
            }
        }

        throw new AgentLoopException($"Agent loop reached max depth ({maxDepth}). Anti-loop safeguard reached.");
    }
    
    private async Task<ToolMessage[]> ProcessToolCalls(
        IEnumerable<AgentResponse> responseMessages, CancellationToken cancellationToken)
    {
        var toolTasks = responseMessages
            .Where(x => x.StopReason == StopReason.ToolCalls)
            .SelectMany(x => x.ToolCalls)
            .Select(x => ResolveToolRequest(_tools[x.Name], x, cancellationToken));
        return await Task.WhenAll(toolTasks);
    }

    private async Task<ToolMessage> ResolveToolRequest(
        ITool tool, ToolCall toolCall, CancellationToken cancellationToken)
    {
        var definition = tool.GetToolDefinition();
        try
        {
            var toolMessage = await tool.Run(toolCall, cancellationToken);
            logger.LogInformation(
                "Tool {ToolName} with {Params} : {toolResponse}",
                definition.Name, toolCall.Parameters, toolMessage.Content);
            return toolMessage;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Tool {ToolName} with {Params} Error: {ExceptionMessage}",
                definition.Name, toolCall.Parameters, ex.Message);

            return new ToolMessage
            {
                Role = Role.Tool,
                Content = new JsonObject
                {
                    ["status"] = "success",
                    ["message"] = $"There was an error on tool call {toolCall.Id}: {ex.Message}"
                }.ToJsonString(),
                ToolCallId = toolCall.Id
            };
        }
    }
    
    private async Task<bool> TryProcessLongRunningTasks(IEnumerable<Task<Message?>> longRunningTasks)
    {
        var messages = (await Task.WhenAll(longRunningTasks))
            .Where(x => x is not null)
            .Select(x => x!)
            .ToArray();
        UpdateConversation(messages);
        return messages.Length > 0;
    }

    private CancellationToken GetChildCancellationToken(bool cancelPrevious, CancellationToken cancellationToken)
    {
        if (cancelPrevious)
        {
            foreach (var tokenSource in _cancelTokenSources.Values)
            {
                tokenSource.Cancel();
                tokenSource.Dispose();
            }
            _cancelTokenSources.Clear();
        }

        if (_cancelTokenSources.TryGetValue(cancellationToken, out var value))
        {
            return value.Token;
        }
        var newSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancelTokenSources[cancellationToken] = newSource;
        return newSource.Token;
    }

    private ImmutableArray<Message> GetConversationSnapshot()
    {
        lock (_messagesLock)
        {
            return [.._messages];
        }
    }
    private void UpdateConversation(IEnumerable<Message> messages)
    {
        lock (_messagesLock)
        {
            _messages.AddRange(messages);
        }
    }
}