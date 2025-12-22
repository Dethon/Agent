using System.Runtime.CompilerServices;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Agents.AI;
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
                .GroupByStreaming(async (x, ct) => await CreateTopicIfNeeded(x, ct), cancellationToken)
                .Select(group => ProcessChatThread(group.Key, group, cancellationToken))
                .Merge(cancellationToken);

            await foreach (var (key, aiResponse) in responses)
            {
                try
                {
                    await SendResponse(key, aiResponse, cancellationToken);
                }
                catch (Exception ex)
                {
                    if (logger.IsEnabled(LogLevel.Error))
                    {
                        logger.LogError(ex, "Inner ChatMonitor exception: {exceptionMessage}", ex.Message);
                    }
                }
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
        AgentKey agentKey, IAsyncGrouping<AgentKey, ChatPrompt> group, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var agent = agentFactory.Create(agentKey);
        var context = await threadResolver.ResolveAsync(agentKey, ct);
        var thread = GetOrRestoreThread(agent, context);

        context.RegisterCompletionCallback(group.Complete);

        using var linkedCts = context.GetLinkedTokenSource(ct);
        var linkedCt = linkedCts.Token;

        // ReSharper disable once AccessToDisposedClosure - agent and threadCts are disposed after await foreach completes
        var aiResponses = group
            .Select(async (x, _, _) =>
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

                await threadResolver.CleanAsync(agentKey);
                return AsyncEnumerable.Empty<AiResponse>();
            })
            .Merge(linkedCt);

        await foreach (var aiResponse in aiResponses)
        {
            await context.SaveThreadAsync(thread.Serialize(), linkedCt);
            yield return (agentKey, aiResponse);
        }
    }

    private static AgentThread GetOrRestoreThread(DisposableAgent agent, ChatThreadContext context)
    {
        return context.PersistedThread is { } persisted
            ? agent.DeserializeThread(persisted)
            : agent.GetNewThread();
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
            Message = prompt.Prompt.TrimStart('/'),
            Bold = true
        };
        await chatMessengerClient.SendResponse(prompt.ChatId, responseMessage, threadId, cancellationToken);

        return new AgentKey(prompt.ChatId, threadId);
    }

    private async Task SendResponse(AgentKey agentKey, AiResponse response, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(response.Content))
            {
                return;
            }

            var responseMessage = new ChatResponseMessage
            {
                Message = response.Content
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