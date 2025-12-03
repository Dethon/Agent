using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

public interface IMcpAgentFactory
{
    Task<CancellableAiAgent> Create(CancellationToken ct);
}

public class ChatMonitor(
    AgentResolver agentResolver,
    TaskQueue queue,
    IChatMessengerClient chatMessengerClient,
    IMcpAgentFactory agentFactory,
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
            if (logger.IsEnabled(LogLevel.Error))
            {
                if (logger.IsEnabled(LogLevel.Error))
                {
                    logger.LogError(ex, "ChatMonitor exception: {exceptionMessage}", ex.Message);
                }
            }
        }
    }

    private async Task AgentTask(ChatPrompt prompt, CancellationToken cancellationToken)
    {
        prompt = await CreateTopicIfNeeded(prompt, cancellationToken);
        var agent = await agentResolver.Resolve(
            prompt.ChatId,
            prompt.ThreadId,
            agentFactory.Create,
            cancellationToken);

        if (prompt.IsCommand && prompt.Prompt.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            agent.CancelCurrentExecution();
            return;
        }

        var channelReader = agent.Subscribe(true);
        await chatMessengerClient.BlockWhile(
            prompt.ChatId,
            prompt.ThreadId,
            async ct =>
            {
                var mainAiResponses = agent
                    .RunStreamingAsync(prompt.Prompt, cancellationToken: ct)
                    .ToUpdateAiResponsePairs();
                var notificationAiResponses = channelReader
                    .ReadAllAsync(ct)
                    .ToUpdateAiResponsePairs();
                var aiResponses = mainAiResponses
                    .Concat(notificationAiResponses)
                    .Where(x => x.Item2 is not null)
                    .Select(x => x.Item2)
                    .Cast<AiResponse>()
                    .WithCancellation(ct);

                await foreach (var aiResponse in aiResponses)
                {
                    await ProcessResponse(prompt, aiResponse, ct);
                }
            },
            cancellationToken);
    }

    private async Task<ChatPrompt> CreateTopicIfNeeded(ChatPrompt prompt, CancellationToken cancellationToken)
    {
        if (prompt.ThreadId is not null)
        {
            return prompt;
        }

        var threadId = await chatMessengerClient.CreateThread(prompt.ChatId, prompt.Prompt, cancellationToken);
        var responseMessage = new ChatResponseMessage
        {
            Message = prompt.Prompt,
            Bold = true
        };
        await chatMessengerClient.SendResponse(prompt.ChatId, responseMessage, threadId, cancellationToken);

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
            var responseMessage = new ChatResponseMessage
            {
                Message = response.Content,
                CalledTools = response.ToolCalls
            };
            await chatMessengerClient.SendResponse(prompt.ChatId, responseMessage, prompt.ThreadId, ct);
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error))
            {
                logger.LogError(ex,
                    "Error writing response ChatId: {chatId}, ThreadId: {threadId}: {exceptionMessage}",
                    prompt.ChatId, prompt.ThreadId, ex.Message);
            }
        }
    }
}