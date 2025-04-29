using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Exceptions;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;

namespace Domain.Agents;

public abstract class BaseAgent(
    ILargeLanguageModel largeLanguageModel,
    int maxDepth,
    ILogger<BaseAgent> logger) : IAgent
{
    [PublicAPI] public int MaxDepth { get; set; } = maxDepth;
    public List<Message> Messages { get; protected init; } = [];

    public abstract IAsyncEnumerable<AgentResponse> Run(
        string userPrompt, CancellationToken cancellationToken = default);

    protected async IAsyncEnumerable<AgentResponse> ExecuteAgentLoop(
        Dictionary<string, ITool> tools,
        float? temperature = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var toolDefinitions = tools.Values
            .Select(x => x.GetToolDefinition())
            .ToArray();
        for (var i = 0; i < MaxDepth; i++)
        {
            var responseMessages = await largeLanguageModel.Prompt(
                Messages, toolDefinitions, temperature, cancellationToken);
            foreach (var responseMessage in responseMessages)
            {
                yield return responseMessage;
            }

            var toolTasks = responseMessages
                .Where(x => x.StopReason == StopReason.ToolCalls)
                .SelectMany(x => x.ToolCalls)
                .Select(x => ResolveToolRequest(tools[x.Name], x, cancellationToken))
                .ToArray();
            var toolResponseMessages = await Task.WhenAll(toolTasks);
            Messages.AddRange(responseMessages);
            Messages.AddRange(toolResponseMessages);

            if (toolTasks.Length == 0)
            {
                yield break;
            }
        }

        throw new AgentLoopException($"Agent loop reached max depth ({MaxDepth}). Anti-loop safeguard reached.");
    }

    private async Task<ToolMessage> ResolveToolRequest(
        ITool tool, ToolCall toolCall, CancellationToken cancellationToken)
    {
        try
        {
            var toolResponse = await tool.Run(toolCall.Parameters, cancellationToken);
            logger.LogInformation("Tool {ToolName} : {toolResponse}", tool.Name, toolResponse);
            return new ToolMessage
            {
                Role = Role.Tool,
                Content = toolResponse.ToJsonString(),
                ToolCallId = toolCall.Id
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Tool {ToolName} Error: {ExceptionMessage}", tool.Name, ex.Message);
            return new ToolMessage
            {
                Role = Role.Tool,
                Content = $"There was an error. Exception: {ex.Message}",
                ToolCallId = toolCall.Id
            };
        }
    }
}