using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

public class ScheduleExecutor(
    IScheduleStore store,
    IScheduleAgentFactory agentFactory,
    IReadOnlyList<IChannelConnection> channels,
    string? defaultScheduleChannelId,
    Func<IChannelConnection, string, IToolApprovalHandler> approvalHandlerFactory,
    Channel<Schedule> scheduleChannel,
    ILogger<ScheduleExecutor> logger)
{
    public async Task ProcessSchedulesAsync(CancellationToken ct)
    {
        await foreach (var schedule in scheduleChannel.Reader.ReadAllAsync(ct))
        {
            await ProcessScheduleAsync(schedule, ct);
        }
    }

    private async Task ProcessScheduleAsync(Schedule schedule, CancellationToken ct)
    {
        var targetChannel = defaultScheduleChannelId is not null
            ? channels.FirstOrDefault(c => c.ChannelId == defaultScheduleChannelId)
            : null;

        var conversationId = targetChannel is not null
            ? await TryCreateConversation(targetChannel, schedule, ct)
            : null;

        if (targetChannel is not null && conversationId is not null)
        {
            await ExecuteWithNotifications(schedule, targetChannel, conversationId, ct);
        }
        else
        {
            await ExecuteSilently(schedule, ct);
        }

        if (schedule.CronExpression is null)
        {
            await store.DeleteAsync(schedule.Id, ct);
        }
    }

    private async Task<string?> TryCreateConversation(
        IChannelConnection channel, Schedule schedule, CancellationToken ct)
    {
        try
        {
            return await channel.CreateConversationAsync(
                schedule.Agent.Id,
                "Scheduled task",
                schedule.UserId ?? "scheduler",
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error creating conversation for schedule {ScheduleId}", schedule.Id);
            return null;
        }
    }

    private async Task ExecuteWithNotifications(
        Schedule schedule, IChannelConnection channel, string conversationId, CancellationToken ct)
    {
        var agentKey = new AgentKey(conversationId, schedule.Agent.Id);

        logger.LogInformation(
            "Executing schedule {ScheduleId} for agent {AgentName} on conversation {ConversationId}",
            schedule.Id,
            schedule.Agent.Name,
            conversationId);

        var approvalHandler = approvalHandlerFactory(channel, conversationId);

        await foreach (var (update, _) in ExecuteScheduleCore(schedule, agentKey, approvalHandler, ct))
        {
            var (content, contentType, isComplete) = MapResponseUpdate(update);
            await channel.SendReplyAsync(conversationId, content, contentType, isComplete, update.MessageId, ct);
        }
    }

    private async Task ExecuteSilently(Schedule schedule, CancellationToken ct)
    {
        logger.LogInformation(
            "Executing schedule {ScheduleId} for agent {AgentName} silently (no channel available)",
            schedule.Id,
            schedule.Agent.Name);

        var agentKey = new AgentKey("scheduled", schedule.Agent.Id);
        var approvalHandler = approvalHandlerFactory(
            channels.FirstOrDefault()!,
            "scheduled");

        await foreach (var _ in ExecuteScheduleCore(schedule, agentKey, approvalHandler, ct))
        {
            // Consume the stream silently
        }
    }

    private async IAsyncEnumerable<(AgentResponseUpdate Update, AiResponse? AiResponse)> ExecuteScheduleCore(
        Schedule schedule,
        AgentKey agentKey,
        IToolApprovalHandler approvalHandler,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var agent = agentFactory.CreateFromDefinition(
            agentKey,
            schedule.UserId ?? "scheduler",
            schedule.Agent,
            approvalHandler);

        var thread = await agent.DeserializeSessionAsync(
            JsonSerializer.SerializeToElement(agentKey.ToString()),
            null,
            ct);

        var userMessage = new ChatMessage(ChatRole.User, schedule.Prompt);
        userMessage.SetSenderId(schedule.UserId);
        userMessage.SetTimestamp(DateTimeOffset.UtcNow);

        await foreach (var (update, aiResponse) in agent
                           .RunStreamingAsync([userMessage], thread, cancellationToken: ct)
                           .WithErrorHandling(ct)
                           .ToUpdateAiResponsePairs()
                           .WithCancellation(ct))
        {
            yield return (update, aiResponse);
        }

        yield return (new AgentResponseUpdate { Contents = [new StreamCompleteContent()] }, null);
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
}
