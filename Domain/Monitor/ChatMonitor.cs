using System.Runtime.CompilerServices;
using System.Text.Json;
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
                .Merge(cancellationToken)
                .Select((x, i) => (x.Item1, x.Item2, i));

            await foreach (var (key, aiResponse, i) in responses)
            {
                try
                {
                    await SendResponse(key, aiResponse, i, cancellationToken);
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
        AgentKey agentKey,
        IAsyncGrouping<AgentKey, ChatPrompt> group,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var firstPrompt = await group.FirstAsync(ct);
        await using var agent = agentFactory.Create(agentKey, firstPrompt.Sender, firstPrompt.BotTokenHash);
        var context = threadResolver.Resolve(agentKey);
        var thread = GetOrRestoreThread(agent, agentKey);

        context.RegisterCompletionCallback(group.Complete);

        using var linkedCts = context.GetLinkedTokenSource(ct);
        var linkedCt = linkedCts.Token;

        // ReSharper disable once AccessToDisposedClosure - agent and threadCts are disposed after await foreach completes
        var aiResponses = group.Prepend(firstPrompt)
            .Select(async (x, _, _) =>
            {
                var command = ChatCommandParser.Parse(x.Prompt);
                switch (command)
                {
                    case ChatCommand.Clear:
                        await threadResolver.ClearAsync(agentKey);
                        return AsyncEnumerable.Empty<AiResponse>();
                    case ChatCommand.Cancel:
                        threadResolver.Cancel(agentKey);
                        return AsyncEnumerable.Empty<AiResponse>();
                    default:
                        // Append IsComplete after each agent run finishes
                        // Wrap in error handling to convert exceptions to error responses
                        return WrapWithErrorHandling(
                            agent
                                .RunStreamingAsync(x.Prompt, thread, cancellationToken: linkedCt)
                                .ToUpdateAiResponsePairs()
                                .Where(y => y.Item2 is not null)
                                .Select(y => y.Item2)
                                .Cast<AiResponse>(),
                            linkedCt);
                }
            })
            .Merge(linkedCt);

        await foreach (var response in aiResponses.WithCancellation(ct))
        {
            yield return (agentKey, response);
        }
    }

    private static AgentThread GetOrRestoreThread(DisposableAgent agent, AgentKey agentKey)
    {
        return agent.DeserializeThread(JsonSerializer.SerializeToElement(agentKey.ToString()));
    }

    private async Task<AgentKey> CreateTopicIfNeeded(ChatPrompt prompt, CancellationToken cancellationToken)
    {
        if (prompt.ThreadId is not null)
        {
            return new AgentKey(prompt.ChatId, prompt.ThreadId.Value, prompt.BotTokenHash);
        }

        var threadId = await chatMessengerClient.CreateThread(
            prompt.ChatId, prompt.Prompt, prompt.BotTokenHash, cancellationToken);
        var responseMessage = new ChatResponseMessage
        {
            Message = prompt.Prompt.TrimStart('/'),
            Bold = true
        };
        await chatMessengerClient.SendResponse(
            prompt.ChatId, responseMessage, threadId, prompt.BotTokenHash, cancellationToken);

        return new AgentKey(prompt.ChatId, threadId, prompt.BotTokenHash);
    }

    private async Task SendResponse(AgentKey agentKey, AiResponse response, int idx, CancellationToken ct)
    {
        try
        {
            if (response.IsComplete)
            {
                await chatMessengerClient.SendResponse(
                    agentKey.ChatId,
                    new ChatResponseMessage { IsComplete = true, Error = response.Error },
                    agentKey.ThreadId,
                    agentKey.BotTokenHash,
                    ct);
                return;
            }

            if (string.IsNullOrEmpty(response.Content) && string.IsNullOrEmpty(response.Reasoning))
            {
                return;
            }

            var responseMessage = new ChatResponseMessage
            {
                Message = response.Content,
                Reasoning = response.Reasoning,
                MessageIndex = idx
            };
            await chatMessengerClient.SendResponse(
                agentKey.ChatId, responseMessage, agentKey.ThreadId, agentKey.BotTokenHash, ct);
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

    private static async IAsyncEnumerable<AiResponse> WrapWithErrorHandling(
        IAsyncEnumerable<AiResponse> source,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var enumerator = source.GetAsyncEnumerator(ct);
        AiResponse? errorResponse = null;
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    errorResponse = new AiResponse
                    {
                        Error = $"An error occurred: {ex.Message}",
                        IsComplete = true
                    };
                    break;
                }

                if (!hasNext)
                {
                    break;
                }

                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        if (errorResponse is not null)
        {
            yield return errorResponse;
        }
        else
        {
            // Append completion marker if no error
            yield return new AiResponse { IsComplete = true };
        }
    }
}