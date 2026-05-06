using System.Runtime.CompilerServices;
using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.DTOs.SubAgent;
using Domain.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

public class ChatMonitor(
    IReadOnlyList<IChannelConnection> channels,
    IAgentFactory agentFactory,
    Func<IChannelConnection, string, IToolApprovalHandler> approvalHandlerFactory,
    ChatThreadResolver threadResolver,
    IMetricsPublisher metricsPublisher,
    IMemoryRecallHook? memoryRecallHook,
    ILogger<ChatMonitor> logger,
    ISubAgentSessionsRegistry? subAgentRegistry = null)
{
    public async Task Monitor(CancellationToken cancellationToken)
    {
        foreach (var ch in channels)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var req in ch.SubAgentCancelRequests.WithCancellation(cancellationToken))
                    {
                        if (subAgentRegistry?.TryGetByConversation(req.ConversationId, out var sessions) == true)
                            sessions.Cancel(req.Handle, Domain.DTOs.SubAgent.SubAgentCancelSource.User);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    logger.LogError(ex, "SubAgentCancelRequests subscription failed for channel {ChannelId}", ch.ChannelId);
                }
            }, cancellationToken);
        }

        try
        {
            var merged = channels
                .Select(ch => ch.Messages.Select(m => (Channel: ch, Message: m)))
                .Merge(cancellationToken);

            var groups = merged
                .GroupByStreaming(
                    (x, _) => ValueTask.FromResult(new AgentKey(x.Message.ConversationId, x.Message.AgentId)),
                    cancellationToken)
                .Select(group => ProcessChatThread(group.Key, group, cancellationToken))
                .Merge(cancellationToken);

            await foreach (var _ in groups)
            { }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ChatMonitor exception: {exceptionMessage}", ex.Message);
            await metricsPublisher.PublishAsync(new ErrorEvent
            {
                Service = "agent",
                ErrorType = ex.GetType().Name,
                Message = ex.Message
            });
        }
    }

    private async IAsyncEnumerable<bool> ProcessChatThread(
        AgentKey agentKey,
        IAsyncGrouping<AgentKey, (IChannelConnection Channel, ChannelMessage Message)> group,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var first = await group.FirstAsync(ct);
        var approvalHandler = approvalHandlerFactory(first.Channel, first.Message.ConversationId);
        await using var agent = agentFactory.Create(agentKey, first.Message.Sender, first.Message.AgentId, approvalHandler);
        var context = threadResolver.Resolve(agentKey);
        var thread = await GetOrRestoreThread(agent, agentKey, ct);

        context.RegisterCompletionCallback(group.Complete);

        var sessionManager = subAgentRegistry?.GetOrCreate(agentKey);
        sessionManager?.RebindReply(first.Channel, first.Message.ConversationId);

        using var linkedCts = context.GetLinkedTokenSource(ct);
        var linkedCt = linkedCts.Token;

        var aiResponses = group.Prepend(first)
            .Select(async (x, _, _) =>
            {
                var command = ChatCommandParser.Parse(x.Message.Content);
                switch (command)
                {
                    case ChatCommand.Clear:
                        await threadResolver.ClearAsync(agentKey);
                        return AsyncEnumerable.Empty<(
                            AgentResponseUpdate Update,
                            AiResponse? Response,
                            IChannelConnection Channel,
                            string ConversationId)>();
                    case ChatCommand.Cancel:
                        threadResolver.Cancel(agentKey);
                        return AsyncEnumerable.Empty<(
                            AgentResponseUpdate Update,
                            AiResponse? Response,
                            IChannelConnection Channel,
                            string ConversationId)>();
                    default:
                        var userMessage = new ChatMessage(ChatRole.User, x.Message.Content);
                        userMessage.SetSenderId(x.Message.Sender);
                        userMessage.SetTimestamp(DateTimeOffset.UtcNow);
                        if (memoryRecallHook is not null)
                        {
                            await memoryRecallHook.EnrichAsync(userMessage, x.Message.Sender, x.Message.ConversationId, x.Message.AgentId, thread, linkedCt);
                        }
                        // ReSharper disable once AccessToDisposedClosure
                        return agent
                            .RunStreamingAsync([userMessage], thread, cancellationToken: linkedCt)
                            .WithErrorHandling(linkedCt)
                            .ToUpdateAiResponsePairs()
                            .Append((
                                new AgentResponseUpdate { Contents = [new StreamCompleteContent()] }, null))
                            .Select(pair => (pair.Item1, pair.Item2, x.Channel, x.Message.ConversationId));
                }
            })
            .Merge(linkedCt);

        sessionManager?.SetParentTurnActive(true);
        try
        {
            await foreach (var (update, _, channel, conversationId) in aiResponses.WithCancellation(ct))
            {
                foreach (var mapped in MapResponseUpdate(update))
                {
                    await channel.SendReplyAsync(
                        conversationId, mapped.Content, mapped.ContentType, mapped.IsComplete, update.MessageId, ct);
                }

                foreach (var error in update.Contents.OfType<ErrorContent>())
                {
                    await metricsPublisher.PublishAsync(new ErrorEvent
                    {
                        Service = "agent",
                        ErrorType = error.ErrorCode ?? "Unknown",
                        Message = error.Message
                    }, ct);
                }

                yield return true;
            }
        }
        finally
        {
            sessionManager?.SetParentTurnActive(false);
        }
    }

    private static IEnumerable<(string Content, ReplyContentType ContentType, bool IsComplete)> MapResponseUpdate(
        AgentResponseUpdate update)
    {
        foreach (var aiContent in update.Contents)
        {
            (string, ReplyContentType, bool)? mapped = aiContent switch
            {
                TextContent text when !string.IsNullOrEmpty(text.Text)
                    => (text.Text, ReplyContentType.Text, false),
                TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text)
                    => (reasoning.Text, ReplyContentType.Reasoning, false),
                // FunctionCallContent is intentionally skipped — tool calls are displayed
                // by the approval flow (request_approval tool with mode=request or mode=notify)
                ErrorContent error
                    => (error.Message, ReplyContentType.Error, false),
                StreamCompleteContent
                    => (string.Empty, ReplyContentType.StreamComplete, true),
                _ => null
            };

            if (mapped is { } value)
            {
                yield return value;
            }
        }
    }

    private static ValueTask<AgentSession> GetOrRestoreThread(
        DisposableAgent agent, AgentKey agentKey, CancellationToken ct)
    {
        return agent.DeserializeSessionAsync(JsonSerializer.SerializeToElement(agentKey.ToString()), null, ct);
    }
}