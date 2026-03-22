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
    IReadOnlyList<IChannelConnection> channels,
    IAgentFactory agentFactory,
    Func<IChannelConnection, string, IToolApprovalHandler> approvalHandlerFactory,
    ChatThreadResolver threadResolver,
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

            await foreach (var _ in groups.WithCancellation(cancellationToken)) { }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ChatMonitor exception: {exceptionMessage}", ex.Message);
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
                        return AsyncEnumerable.Empty<(AgentResponseUpdate Update, AiResponse? Response, IChannelConnection Channel, string ConversationId)>();
                    case ChatCommand.Cancel:
                        threadResolver.Cancel(agentKey);
                        return AsyncEnumerable.Empty<(AgentResponseUpdate Update, AiResponse? Response, IChannelConnection Channel, string ConversationId)>();
                    default:
                        var userMessage = new ChatMessage(ChatRole.User, x.Message.Content);
                        userMessage.SetSenderId(x.Message.Sender);
                        userMessage.SetTimestamp(DateTimeOffset.UtcNow);
                        return agent
                            .RunStreamingAsync([userMessage], thread, cancellationToken: linkedCt)
                            .WithErrorHandling(linkedCt)
                            .ToUpdateAiResponsePairs()
                            .Append((
                                new AgentResponseUpdate { Contents = [new StreamCompleteContent()] },
                                (AiResponse?)null))
                            .Select(pair => (pair.Item1, pair.Item2, x.Channel, x.Message.ConversationId));
                }
            })
            .Merge(linkedCt);

        await foreach (var (update, _, channel, conversationId) in aiResponses.WithCancellation(ct))
        {
            var (content, contentType, isComplete) = MapResponseUpdate(update);
            await channel.SendReplyAsync(conversationId, content, contentType, isComplete, update.MessageId, ct);
            yield return true;
        }
    }

    private static (string Content, string ContentType, bool IsComplete) MapResponseUpdate(AgentResponseUpdate update)
    {
        var aiContent = update.Contents.FirstOrDefault();
        return aiContent switch
        {
            TextContent text => (text.Text ?? string.Empty, ReplyContentType.Text, false),
            TextReasoningContent reasoning => (reasoning.Text ?? string.Empty, ReplyContentType.Reasoning, false),
            FunctionCallContent functionCall => (
                JsonSerializer.Serialize(new { functionCall.Name, functionCall.Arguments }),
                ReplyContentType.ToolCall,
                false),
            ErrorContent error => (error.Message ?? string.Empty, ReplyContentType.Error, false),
            StreamCompleteContent => (string.Empty, ReplyContentType.StreamComplete, true),
            _ => (string.Empty, ReplyContentType.Text, false)
        };
    }

    private static ValueTask<AgentSession> GetOrRestoreThread(
        DisposableAgent agent, AgentKey agentKey, CancellationToken ct)
    {
        return agent.DeserializeSessionAsync(JsonSerializer.SerializeToElement(agentKey.ToString()), null, ct);
    }
}
