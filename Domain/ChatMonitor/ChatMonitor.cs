using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.DependencyInjection;

namespace Domain.ChatMonitor;

public class ChatMonitor(
    IServiceProvider services, TaskQueue queue, IChatClient chatClient)
{
    public async Task Monitor(CancellationToken cancellationToken = default)
    {
        var prompts = chatClient.ReadPrompts(1000, cancellationToken);
        await foreach (var prompt in prompts)
        {
            await queue.QueueTask((c) => AgentTask(prompt, c));
        }
    }

    private async Task AgentTask(ChatPrompt prompt, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var agentResolver = scope.ServiceProvider.GetRequiredService<AgentResolver>();
        var agent = agentResolver.Resolve(AgentType.Download);
        var responses = agent.Run(prompt.Prompt, cancellationToken);
        await foreach (var response in responses)
        {
            var toolMessages = response.ToolCalls.Select(x => x.ToString()).ToArray();
            if(!string.IsNullOrEmpty(response.Content))
            {
                await chatClient.SendResponse(prompt.ChatId, response.Content, cancellationToken);
            }
            if (toolMessages.Length > 0)
            {
                await chatClient.SendResponse(prompt.ChatId, string.Join('\n', toolMessages), cancellationToken);
            }
        }
    }
}