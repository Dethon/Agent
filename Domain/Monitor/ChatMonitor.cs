using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

public class ChatMonitor(
    IServiceProvider services,
    TaskQueue queue,
    IChatMessengerClient chatMessengerClient,
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
                (m, ct) => ProcessResponse(prompt, m, ct),
                cancellationToken);

            if (prompt.IsCommand)
            {
                agent.CancelCurrentExecution(true);
            }

            await agent.Run(prompt.Prompt, cancellationToken);
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

    private async Task ProcessResponse(ChatPrompt prompt, ChatResponse response, CancellationToken ct)
    {
        var messages = response.Messages;
        foreach (var message in messages)
        {
            var contents = message.Contents;
            var normalMessage = string.Join("", contents
                .Where(x => x is TextContent)
                .Cast<TextContent>()
                .Select(x => x.Text.HtmlSanitize())).Left(3900);

            var toolCallMessageContent = string.Join("\n", contents
                .Where(x => x is FunctionCallContent)
                .Cast<FunctionCallContent>()
                .Select(x => $"{x.Name}(\n{JsonSerializer.Serialize(x.Arguments)}\n)")).Left(3800);

            if (!string.IsNullOrWhiteSpace(normalMessage))
            {
                await chatMessengerClient.SendResponse(prompt.ChatId, normalMessage, prompt.ThreadId, ct);
            }

            if (!string.IsNullOrWhiteSpace(toolCallMessageContent))
            {
                var toolMessage = "<blockquote expandable>" +
                                  $"<pre><code class=\"language-json\">{toolCallMessageContent}</code></pre>" +
                                  "</blockquote>";
                await chatMessengerClient.SendResponse(prompt.ChatId, toolMessage, prompt.ThreadId, ct);
            }
        }
    }
}