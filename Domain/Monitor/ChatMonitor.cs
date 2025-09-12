using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

public class ChatMonitor(
    AgentResolver agentResolver,
    TaskQueue queue,
    IChatMessengerClient chatMessengerClient,
    IAgentFactory agentFactory,
    ILogger<ChatMonitor> logger)
{
    public async Task Monitor(CancellationToken cancellationToken)
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
        prompt = await CreateTopicIfNeeded(prompt, cancellationToken);
        var responseCallback = (AiResponse r, CancellationToken ct) => ProcessResponse(prompt, r, ct);
        var agent = await agentResolver.Resolve(
            prompt.ChatId,
            prompt.ThreadId,
            ct => agentFactory.Create(responseCallback, ct),
            cancellationToken);

        if (prompt.IsCommand)
        {
            agent.CancelCurrentExecution();
        }

        await chatMessengerClient.DisableChat(prompt.ChatId, prompt.ThreadId, cancellationToken);
        await agent.Run([prompt.Prompt], cancellationToken);
        await chatMessengerClient.EnableChat(prompt.ChatId, prompt.ThreadId, cancellationToken);
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
        try
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
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Error writing response ChatId: {chatId}, ThreadId: {threadId}: {exceptionMessage}",
                prompt.ChatId, prompt.ThreadId, ex.Message);
        }
    }
}