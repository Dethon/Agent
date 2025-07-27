using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Exceptions;
using Microsoft.Extensions.Logging;

namespace Domain.Agents;

public class Agent(
    Message[] messages,
    ILargeLanguageModel largeLanguageModel,
    ITool[] tools,
    int maxDepth,
    bool enableSearch,
    ILogger<Agent> logger) : IAgent
{
    private CancellationTokenSource? _childCancelTokenSource;
    private readonly Lock _messagesLock = new();

    private readonly Dictionary<string, ITool> _tools =
        new(tools.ToDictionary(x => x.GetToolDefinition().Name, x => x));

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
        CancellationToken childCancellationToken;
        lock (_messagesLock)
        {
            _messages.AddRange(prompts);
            childCancellationToken = GetChildCancellationToken(cancelCurrentOperation, cancellationToken);
        }

        return ExecuteAgentLoop(0.5f, childCancellationToken);
    }

    private async IAsyncEnumerable<AgentResponse> ExecuteAgentLoop(
        float? temperature = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<Message> messageSnapshot;
        lock (_messagesLock)
        {
            messageSnapshot = _messages.ToList();
        }

        var toolDefinitions = _tools.Values
            .Select(x => x.GetToolDefinition())
            .ToArray();

        for (var i = 0; i < maxDepth && !cancellationToken.IsCancellationRequested; i++)
        {
            var responseMessages = await largeLanguageModel.Prompt(
                messageSnapshot, toolDefinitions, enableSearch, temperature, cancellationToken);
            foreach (var responseMessage in responseMessages)
            {
                yield return responseMessage;
            }

            var toolTasks = responseMessages
                .Where(x => x.StopReason == StopReason.ToolCalls)
                .SelectMany(x => x.ToolCalls)
                .Select(x => ResolveToolRequest(_tools[x.Name], x, cancellationToken))
                .ToArray();
            var toolResponseMessages = await Task.WhenAll(toolTasks);
            lock (_messagesLock)
            {
                _messages.AddRange(responseMessages);
                _messages.AddRange(toolResponseMessages);
                messageSnapshot = _messages.ToList();
            }

            if (toolTasks.Length == 0)
            {
                yield break;
            }
        }

        throw new AgentLoopException($"Agent loop reached max depth ({maxDepth}). Anti-loop safeguard reached.");
    }

    private async Task<ToolMessage> ResolveToolRequest(
        ITool tool, ToolCall toolCall, CancellationToken cancellationToken)
    {
        var definition = tool.GetToolDefinition();
        try
        {
            var toolResponse = await tool.Run(toolCall.Parameters, cancellationToken);
            logger.LogInformation(
                "Tool {ToolName} with {Params} : {toolResponse}",
                definition.Name, toolCall.Parameters, toolResponse);
            return new ToolMessage
            {
                Role = Role.Tool,
                Content = toolResponse.ToJsonString(),
                ToolCallId = toolCall.Id
            };
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

    private CancellationToken GetChildCancellationToken(bool cancelCurrent, CancellationToken cancellationToken)
    {
        if (_childCancelTokenSource is null)
        {
            _childCancelTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            return _childCancelTokenSource.Token;
        }

        if (!cancelCurrent)
        {
            return _childCancelTokenSource.Token;
        }
        
        _childCancelTokenSource.Cancel();
        _childCancelTokenSource.Dispose();
        _childCancelTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        return _childCancelTokenSource.Token;
    }
}