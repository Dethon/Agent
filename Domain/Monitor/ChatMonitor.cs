using System.Collections.Concurrent;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

public class ChatMonitor(
    ThreadResolver threadResolver,
    TaskQueue queue,
    IChatMessengerClient chatMessengerClient,
    Func<CancellationToken, Task<AIAgent>> agentFactory,
    ILogger<ChatMonitor> logger)
{
    private readonly ConcurrentDictionary<AgentKey, CancellationTokenSource> _cancellationSources = new();

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
        var agentKey = await CreateTopicIfNeeded(prompt, cancellationToken);
        var cts = _cancellationSources.GetOrAdd(agentKey, _ => new CancellationTokenSource());
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);
        var ct = linkedCts.Token;

        var agent = await agentFactory(ct);
        var thread = await threadResolver.Resolve(agentKey, () => agent.GetNewThread(), ct);

        if (prompt.Prompt.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
        {
            _cancellationSources.TryRemove(agentKey, out _);
            await cts.CancelAsync();
            return;
        }

        var aiResponses = agent
            .RunStreamingAsync(prompt.Prompt, thread, cancellationToken: ct)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2)
            .Cast<AiResponse>()
            .WithCancellation(ct);

        await chatMessengerClient.BlockWhile(
            agentKey.ChatId,
            agentKey.ThreadId,
            async c =>
            {
                await foreach (var aiResponse in aiResponses)
                {
                    await ProcessResponse(agentKey, aiResponse, c);
                }

                Console.WriteLine($"{agentKey.ChatId}: {prompt.Prompt}");
            },
            ct);
        Console.WriteLine($"{agentKey.ChatId}: {prompt.Prompt}");
    }

    private async Task<AgentKey> CreateTopicIfNeeded(ChatPrompt prompt, CancellationToken cancellationToken)
    {
        if (prompt.ThreadId is not null)
        {
            return new AgentKey(prompt.ChatId, prompt.ThreadId.Value);
        }

        var threadId = await chatMessengerClient.CreateThread(prompt.ChatId, prompt.Prompt, cancellationToken);
        var responseMessage = new ChatResponseMessage
        {
            Message = prompt.Prompt,
            Bold = true
        };
        await chatMessengerClient.SendResponse(prompt.ChatId, responseMessage, threadId, cancellationToken);

        return new AgentKey(prompt.ChatId, threadId);
    }

    private async Task ProcessResponse(AgentKey agentKey, AiResponse response, CancellationToken ct)
    {
        try
        {
            var responseMessage = new ChatResponseMessage
            {
                Message = response.Content,
                CalledTools = response.ToolCalls
            };
            await chatMessengerClient.SendResponse(agentKey.ChatId, responseMessage, agentKey.ThreadId, ct);
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error))
            {
                logger.LogError(ex,
                    "Error writing response ChatId: {chatId}, ThreadId: {threadId}: {exceptionMessage}",
                    agentKey.ChatId, agentKey.ThreadId, ex.Message);
            }
        }
    }
}