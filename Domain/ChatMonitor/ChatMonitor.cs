using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Domain.ChatMonitor;

public class ChatMonitor(
    IServiceProvider services,
    TaskQueue queue,
    IChatClient chatClient,
    ILogger<ChatMonitor> logger)
{
    public async Task Monitor(CancellationToken cancellationToken = default)
    {
        var prompts = chatClient.ReadPrompts(1000, cancellationToken);
        await foreach (var prompt in prompts)
        {
            await queue.QueueTask(c => AgentTask(prompt, c));
        }
    }

    private async Task AgentTask(ChatPrompt prompt, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            var agentResolver = scope.ServiceProvider.GetRequiredService<AgentResolver>();
            var agent = agentResolver.Resolve(AgentType.Download, prompt.ReplyToMessageId);
            var responses = agent.Run(prompt.Prompt, cancellationToken);
            await foreach (var response in responses)
            {
                var mainMessage = response.Content;
                var toolMessage = string.Join('\n', response.ToolCalls.Select(x => x.ToString()));
                var messageLength = mainMessage.Length + toolMessage.Length;

                if (messageLength is 0 or > 3950)
                {
                    continue;
                }

                var message = $"{mainMessage}<blockquote expandable><code>{toolMessage}</code></blockquote>";
                var messageId = await chatClient
                    .SendResponse(prompt.ChatId, message, prompt.MessageId, cancellationToken);

                agentResolver.AssociateMessageToAgent(messageId + prompt.Sender.GetHashCode(), agent);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AgentTask exception: {exceptionMessage}", ex.Message);
        }
    }
}