using System.Runtime.CompilerServices;
using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
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
    ILogger<ChatMonitor> logger)
{
    public async Task Monitor(CancellationToken cancellationToken)
    {
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

            await foreach (var _ in groups) { }
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

        await foreach (var (update, _, channel, conversationId) in aiResponses.WithCancellation(ct))
        {
            foreach (var mapped in MapResponseUpdate(update))
            {
                await channel.SendReplyAsync(
                    conversationId, mapped.Content, mapped.ContentType, mapped.IsComplete, update.MessageId, ct);
            }

            yield return true;
        }
    }

    private static IEnumerable<(string Content, string ContentType, bool IsComplete)> MapResponseUpdate(
        AgentResponseUpdate update)
    {
        foreach (var aiContent in update.Contents)
        {
            var mapped = aiContent switch
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
                _ => default
            };

            if (mapped != default)
            {
                yield return mapped;
            }
        }
    }

    private static ValueTask<AgentSession> GetOrRestoreThread(
        DisposableAgent agent, AgentKey agentKey, CancellationToken ct)
    {
        return agent.DeserializeSessionAsync(JsonSerializer.SerializeToElement(agentKey.ToString()), null, ct);
    }
}
