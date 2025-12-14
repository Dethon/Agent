using System.Runtime.CompilerServices;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

public class ChatMonitor(
    IChatMessengerClient chatMessengerClient,
    IAgentFactory agentFactory,
    ChatThreadResolver threadResolver,
    ILogger<ChatMonitor> logger)
{
    public async Task Monitor(CancellationToken cancellationToken)
    {
        try
        {
            var responses = chatMessengerClient.ReadPrompts(1000, cancellationToken)
                .GroupByStreaming(async (x, ct) => await CreateTopicIfNeeded(x, ct), ct: cancellationToken)
                .Select(group => ProcessChatThread(group.Key, group, cancellationToken))
                .Merge(cancellationToken);

            await foreach (var (key, aiResponse) in responses)
            {
                await SendResponse(key, aiResponse, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error))
            {
                logger.LogError(ex, "ChatMonitor exception: {exceptionMessage}", ex.Message);
            }
        }
    }

    private async IAsyncEnumerable<(AgentKey, AiResponse)> ProcessChatThread(
        AgentKey agentKey, IAsyncEnumerable<ChatPrompt> prompts, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var agent = agentFactory.Create(agentKey);
        var thread = agent.GetNewThread();

        var context = threadResolver.Resolve(agentKey);
        using var linkedCts = context.GetLinkedTokenSource(ct);
        var linkedCt = linkedCts.Token;

        // ReSharper disable once AccessToDisposedClosure - agent and threadCts are disposed after await foreach completes
        var aiResponses = prompts
            .Select(x =>
            {
                if (!x.Prompt.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
                {
                    return agent
                        .RunStreamingAsync(x.Prompt, thread, cancellationToken: linkedCt)
                        .ToUpdateAiResponsePairs()
                        .Where(y => y.Item2 is not null)
                        .Select(y => y.Item2)
                        .Cast<AiResponse>();
                }

                threadResolver.Clean(agentKey);
                return AsyncEnumerable.Empty<AiResponse>();
            })
            .Merge(linkedCt);

        await foreach (var aiResponse in aiResponses)
        {
            yield return (agentKey, aiResponse);
        }
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

    private async Task SendResponse(AgentKey agentKey, AiResponse response, CancellationToken ct)
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