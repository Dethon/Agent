using System.Runtime.CompilerServices;
using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
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
                .GroupByStreaming(
                    async (x, ct) => await chatMessengerClient.CreateTopicIfNeededAsync(
                        x.ChatId, x.ThreadId, x.AgentId, x.Prompt, ct),
                    cancellationToken)
                .Select(group => ProcessChatThread(group.Key, group, cancellationToken))
                .Merge(cancellationToken);

            try
            {
                await chatMessengerClient.ProcessResponseStreamAsync(responses, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Inner ChatMonitor exception: {exceptionMessage}", ex.Message);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ChatMonitor exception: {exceptionMessage}", ex.Message);
        }
    }

    private async IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> ProcessChatThread(
        AgentKey agentKey,
        IAsyncGrouping<AgentKey, ChatPrompt> group,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var firstPrompt = await group.FirstAsync(ct);
        await using var agent = agentFactory.Create(agentKey, firstPrompt.Sender, firstPrompt.AgentId);
        var context = threadResolver.Resolve(agentKey);
        var thread = await GetOrRestoreThread(agent, agentKey, ct);

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
                        return AsyncEnumerable.Empty<(AgentResponseUpdate, AiResponse?)>();
                    case ChatCommand.Cancel:
                        threadResolver.Cancel(agentKey);
                        return AsyncEnumerable.Empty<(AgentResponseUpdate, AiResponse?)>();
                    default:
                        var userMessage = new ChatMessage(ChatRole.User, x.Prompt);
                        userMessage.SetSenderId(x.Sender);
                        return agent
                            .RunStreamingAsync([userMessage], thread, cancellationToken: linkedCt)
                            .WithErrorHandling(linkedCt)
                            .ToUpdateAiResponsePairs()
                            .Append((
                                new AgentResponseUpdate { Contents = [new StreamCompleteContent()] },
                                null));
                }
            })
            .Merge(linkedCt);

        await foreach (var (update, aiResponse) in aiResponses.WithCancellation(ct))
        {
            yield return (agentKey, update, aiResponse);
        }
    }

    private static ValueTask<AgentThread> GetOrRestoreThread(
        DisposableAgent agent, AgentKey agentKey, CancellationToken ct)
    {
        return agent.DeserializeThreadAsync(JsonSerializer.SerializeToElement(agentKey.ToString()), null, ct);
    }
}