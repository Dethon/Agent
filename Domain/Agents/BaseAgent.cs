using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Agents;

public abstract class BaseAgent(ILargeLanguageModel largeLanguageModel)
{
    protected async Task<List<Message>> ExecuteAgentLoop(
        List<Message> messages,
        Dictionary<string, ITool> tools,
        float? temperature = null,
        CancellationToken cancellationToken = default)
    {
        var toolDefinitions = tools.Values
            .Select(x => x.GetToolDefinition())
            .ToArray();
        while (true)
        {
            var responseMessages = await largeLanguageModel.Prompt(
                messages, toolDefinitions, temperature, cancellationToken);
            DisplayResponses(responseMessages);

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
                return messages;
            }
        }
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
            return new ToolMessage
            {
                Role = Role.Tool,
                Content = $"Exception: {ex.Message}",
                ToolCallId = toolCall.Id
            };
        }
    }

    private static void DisplayResponses(AgentResponse[] agentResponses)
    {
        foreach (var message in agentResponses)
        {
            if (!string.IsNullOrEmpty(message.Reasoning))
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine(message.Reasoning);
            }

            if (!string.IsNullOrEmpty(message.Content))
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine(message.Content);
            }

            foreach (var toolCall in message.ToolCalls)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"{toolCall.Name}({toolCall.Parameters?.ToJsonString()})");
            }
        }

        Console.ResetColor();
    }
}