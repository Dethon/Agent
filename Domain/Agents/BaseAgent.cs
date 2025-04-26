using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Exceptions;
using JetBrains.Annotations;

namespace Domain.Agents;

public abstract class BaseAgent(ILargeLanguageModel largeLanguageModel, int maxDepth)
{
    [PublicAPI] public int MaxDepth { get; set; } = maxDepth;
    protected List<Message> _messages = [];

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
                _messages, toolDefinitions, temperature, cancellationToken);
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
            _messages.AddRange(responseMessages);
            _messages.AddRange(toolResponseMessages);

            if (toolTasks.Length == 0)
            {
                yield break;
            }
        }

        throw new AgentLoopException($"Agent loop reached max depth ({MaxDepth}). Anti-loop safeguard reached.");
    }

    private static async Task<ToolMessage> ResolveToolRequest(
        ITool tool, ToolCall toolCall, CancellationToken cancellationToken)
    {
        try
        {
            var toolResponse = await tool.Run(toolCall.Parameters, cancellationToken);
            return new ToolMessage
            {
                Role = Role.Tool,
                Content = toolResponse.ToJsonString(),
                ToolCallId = toolCall.Id
            };
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(ex.Message); // TODO: Change to logger
            Console.ResetColor();
            return new ToolMessage
            {
                Role = Role.Tool,
                Content = $"There was an error. Exception: {ex.Message}",
                ToolCallId = toolCall.Id
            };
        }
    }
}