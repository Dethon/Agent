using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Agents;

public abstract class BaseAgent(ILargeLanguageModel largeLanguageModel)
{
    public async Task<AgentResponse[]> ExecuteAgentLoop(
        List<Message> messages,
        Dictionary<string, ToolDefinition> tools,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var responseMessages = await largeLanguageModel.Prompt(messages, tools.Values, cancellationToken);
            messages.AddRange(responseMessages);

            var toolRequests = responseMessages
                .Where(x => x.StopReason == StopReason.ToolCalls)
                .SelectMany(x => x.ToolCalls)
                .ToArray();
            foreach (var toolRequest in toolRequests)
            {
                var tool = tools[toolRequest.Name];
                // TODO: Implement tool execution logic
            }

            if (toolRequests.Length == 0) return responseMessages;
        }
    }
}