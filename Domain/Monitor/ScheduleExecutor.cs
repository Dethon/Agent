using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
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
    IMetricsPublisher metricsPublisher,
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
        var sw = Stopwatch.StartNew();
        try
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

            sw.Stop();

            await metricsPublisher.PublishAsync(new ScheduleExecutionEvent
            {
                ScheduleId = schedule.Id,
                AgentId = schedule.Agent.Id,
                Prompt = schedule.Prompt,
                DurationMs = sw.ElapsedMilliseconds,
                Success = true
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            logger.LogError(ex, "Error executing schedule {ScheduleId}", schedule.Id);

            await metricsPublisher.PublishAsync(new ScheduleExecutionEvent
            {
                ScheduleId = schedule.Id,
                AgentId = schedule.Agent.Id,
                Prompt = schedule.Prompt,
                DurationMs = sw.ElapsedMilliseconds,
                Success = false,
                Error = ex.Message
            }, ct);

            await metricsPublisher.PublishAsync(new ErrorEvent
            {
                Service = "scheduler",
                AgentId = schedule.Agent.Id,
                ErrorType = ex.GetType().Name,
                Message = ex.Message
            }, ct);
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
            foreach (var (content, contentType, isComplete) in MapResponseUpdate(update))
            {
                await channel.SendReplyAsync(conversationId, content, contentType, isComplete, update.MessageId, ct);
            }
        }
    }

    private async Task ExecuteSilently(Schedule schedule, CancellationToken ct)
    {
        var fallbackChannel = channels.FirstOrDefault();
        if (fallbackChannel is null)
        {
            logger.LogWarning(
                "Skipping schedule {ScheduleId} for agent {AgentName}: no channel connections available",
                schedule.Id,
                schedule.Agent.Name);
            return;
        }

        logger.LogInformation(
            "Executing schedule {ScheduleId} for agent {AgentName} silently (no target channel)",
            schedule.Id,
            schedule.Agent.Name);

        var agentKey = new AgentKey("scheduled", schedule.Agent.Id);
        var approvalHandler = approvalHandlerFactory(fallbackChannel, "scheduled");

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
}