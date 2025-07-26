using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

public class ChatMonitor(
    IServiceProvider services,
    TaskQueue queue,
    IChatClient chatClient,
    ILogger<ChatMonitor> logger)
{
    public async Task Monitor(CancellationToken cancellationToken = default)
    {
        try
        {
            var prompts = chatClient.ReadPrompts(1000, cancellationToken);
            await foreach (var prompt in prompts)
            {
                await queue.QueueTask(c => AgentTask(prompt, c));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ChatMonitor exception: {exceptionMessage}", ex.Message);
        }
    }

    private async Task AgentTask(ChatPrompt prompt, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            var agentResolver = scope.ServiceProvider.GetRequiredService<IAgentResolver>();
            prompt = await CreateTopicIfNeeded(prompt, cancellationToken);
            var agent = await agentResolver.Resolve(AgentType.Download, prompt.ThreadId);
            var responses = agent.Run(prompt.Prompt, cancellationToken);

            await foreach (var response in responses)
            {
                try
                {
                    await ProcessResponse(prompt, response, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "ProcessResponse exception: {exceptionMessage}", ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AgentTask exception: {exceptionMessage}", ex.Message);
        }
    }

    private async Task<ChatPrompt> CreateTopicIfNeeded(ChatPrompt prompt, CancellationToken cancellationToken)
    {
        if(prompt.ThreadId is not null)
        {
            return prompt;
        }

        var threadId = await chatClient.CreateThread(prompt.ChatId, prompt.Prompt, cancellationToken);
        await chatClient.SendResponse(prompt.ChatId, $"<b>{prompt.Prompt}</b>", threadId, cancellationToken);
            
        return prompt with
        {
            ThreadId = threadId 
        };
    }

    private async Task ProcessResponse(
        ChatPrompt prompt, AgentResponse response, CancellationToken cancellationToken)
    {
        var toolMessage = string.Join('\n', response.ToolCalls.Select(x => x.ToString()));
        var message = "<blockquote expandable>" +
                      $"{response.Content.Left(1900).HtmlSanitize()}" +
                      "</blockquote>" +
                      "<blockquote expandable>" +
                      $"<pre><code>StopReason={response.StopReason}</code>\n\n" +
                      $"<code class=\"language-json\">{toolMessage.Left(1900).HtmlSanitize()}</code></pre>" +
                      "</blockquote>";
        await chatClient.SendResponse(prompt.ChatId, message, prompt.ThreadId, cancellationToken);
    }
}