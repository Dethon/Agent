using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

using AgentFactory = Func<Func<AiResponse, CancellationToken, Task>, CancellationToken, Task<IAgent>>; 

public class ChatMonitor(
    IServiceProvider services,
    TaskQueue queue,
    IChatMessengerClient chatMessengerClient,
    AgentFactory agentFactory,
    ILogger<ChatMonitor> logger)
{
    public async Task Monitor(CancellationToken cancellationToken = default)
    {
        try
        {
            var prompts = chatMessengerClient.ReadPrompts(1000, cancellationToken);
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
            var agentResolver = scope.ServiceProvider.GetRequiredService<AgentResolver>();
            prompt = await CreateTopicIfNeeded(prompt, cancellationToken);

            var agent = await agentResolver.Resolve(
                prompt.ThreadId,
                ct => agentFactory((r, ct2) => ProcessResponse(prompt, r, ct2), ct),
                cancellationToken);

            if (prompt.IsCommand)
            {
                agent.CancelCurrentExecution(true);
            }

            await agent.Run([prompt.Prompt], cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AgentTask exception: {exceptionMessage}", ex.Message);
        }
    }

    private async Task<ChatPrompt> CreateTopicIfNeeded(ChatPrompt prompt, CancellationToken cancellationToken)
    {
        if (prompt.ThreadId is not null)
        {
            return prompt;
        }

        var threadId = await chatMessengerClient.CreateThread(prompt.ChatId, prompt.Prompt, cancellationToken);
        await chatMessengerClient.SendResponse(prompt.ChatId, $"<b>{prompt.Prompt}</b>", threadId, cancellationToken);

        return prompt with
        {
            ThreadId = threadId,
            IsCommand = false
        };
    }

    private async Task ProcessResponse(ChatPrompt prompt, AiResponse response, CancellationToken ct)
    {
        var content = response.Content.HtmlSanitize().Left(4096);
        var toolCalls = response.ToolCalls.HtmlSanitize().Left(3800);
        if (!string.IsNullOrWhiteSpace(content))
        {
            await chatMessengerClient.SendResponse(prompt.ChatId, content, prompt.ThreadId, ct);
        }

        if (!string.IsNullOrWhiteSpace(toolCalls))
        {
            var toolMessage = "<blockquote expandable>" +
                              $"<pre><code class=\"language-json\">{toolCalls}</code></pre>" +
                              "</blockquote>";
            await chatMessengerClient.SendResponse(prompt.ChatId, toolMessage, prompt.ThreadId, ct);
        }
    }
}