using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Agents;

public abstract class BaseAgent(ILargeLanguageModel largeLanguageModel)
{
    protected async Task<AgentResponse[]> ExecuteAgentLoop(
        List<Message> messages,
        Dictionary<string, ITool> tools,
        CancellationToken cancellationToken = default)
    {
        var toolDefinitions = tools.Values
            .Select(x => x.GetToolDefinition())
            .ToArray();
        while (true)
        {
            var responseMessages = await largeLanguageModel.Prompt(messages, toolDefinitions, cancellationToken);

            var toolTasks = responseMessages
                .Where(x => x.StopReason == StopReason.ToolCalls)
                .SelectMany(x => x.ToolCalls)
                .Select(x => ResolveToolRequest(tools[x.Name], x, cancellationToken))
                .ToArray();
            var toolResponseMessages = await Task.WhenAll(toolTasks);
            messages.AddRange(responseMessages);
            messages.AddRange(toolResponseMessages);

            if (toolTasks.Length == 0)
            {
                return responseMessages;
            }
        }
    }

    private static async Task<ToolMessage> ResolveToolRequest(
        ITool tool, ToolCall toolCall, CancellationToken cancellationToken)
    {
        // TODO: Handle errors
        var toolResponse = await tool.Run(toolCall.Parameters, cancellationToken);
        return new ToolMessage
        {
            Role = Role.Tool,
            Content = toolResponse.ToJsonString(),
            ToolCallId = toolCall.Id
        };
    }
}