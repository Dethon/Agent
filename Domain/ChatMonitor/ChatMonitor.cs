using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
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
            var referencedMessageId = prompt.ReplyToMessageId + prompt.Sender.GetHashCode();
            var agent = agentResolver.Resolve(AgentType.Download, referencedMessageId);
            var responses = agent.Run(prompt.Prompt, cancellationToken);

            await foreach (var response in responses)
            {
                var mainMessage = response.Content;
                var toolMessage = string.Join('\n', response.ToolCalls.Select(x => x.ToString()));
                if (mainMessage.Length == 0 && toolMessage.Length == 0)
                {
                    continue;
                }

                var trimMainMessage = mainMessage.Left(2000);
                var trimToolMessage = toolMessage.Left(2000);
                var message = $"{trimMainMessage}<blockquote expandable><code>{trimToolMessage}</code></blockquote>";
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