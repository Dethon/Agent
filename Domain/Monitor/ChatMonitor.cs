using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

public class ChatMonitor(
    TaskQueue queue,
    RunningAgentTracker runningAgentTracker,
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
        var conversationId = $"{prompt.ChatId}:{prompt.ThreadId}";

        if (prompt.IsCommand)
        {
            runningAgentTracker.Cancel(conversationId);
            return;
        }

        var responseCallback = (AiResponse r, CancellationToken ct) => ProcessResponse(prompt, r, ct);
        await using var agent = await agentFactory.Create(conversationId, responseCallback, cancellationToken);

        var agentCt = runningAgentTracker.Track(conversationId);
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, agentCt);

        try
        {
            await chatMessengerClient.BlockWhile(
                prompt.ChatId,
                prompt.ThreadId,
                () => agent.Run([prompt.Prompt], linkedCts.Token),
                linkedCts.Token);
        }
        finally
        {
            runningAgentTracker.Untrack(conversationId);
            linkedCts.Dispose();
        }
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
            logger.LogError(ex,
                "Error writing response ChatId: {chatId}, ThreadId: {threadId}: {exceptionMessage}",
                prompt.ChatId, prompt.ThreadId, ex.Message);
        }
    }
}