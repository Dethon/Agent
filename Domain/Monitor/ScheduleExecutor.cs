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
    IChatMessengerClient messengerClient,
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
        AgentKey agentKey;

        if (messengerClient.SupportsScheduledNotifications)
        {
            try
            {
                agentKey = await messengerClient.CreateTopicIfNeededAsync(
                    chatId: null,
                    threadId: null,
                    agentId: schedule.Agent.Id,
                    topicName: "Scheduled task",
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error creating topic for schedule {ScheduleId}", schedule.Id);
                return;
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Executing schedule {ScheduleId} for agent {AgentName} on thread {ThreadId}",
                    schedule.Id,
                    schedule.Agent.Name,
                    agentKey.ThreadId);
            }

            await messengerClient.StartScheduledStreamAsync(agentKey, ct);

            var responses = ExecuteScheduleCore(schedule, agentKey, schedule.UserId, ct);
            await messengerClient.ProcessResponseStreamAsync(
                responses.Select(r => (agentKey, r.Update, r.AiResponse)), ct);
        }
        else
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Executing schedule {ScheduleId} for agent {AgentName} silently (no notification support)",
                    schedule.Id,
                    schedule.Agent.Name);
            }

            agentKey = new AgentKey(0, 0, schedule.Agent.Id);

            await foreach (var _ in ExecuteScheduleCore(schedule, agentKey, schedule.UserId, ct))
            {
                // Consume the stream silently
            }
        }

        if (schedule.CronExpression is null)
        {
            await store.DeleteAsync(schedule.Id, ct);
        }
    }

    private async IAsyncEnumerable<(AgentResponseUpdate Update, AiResponse? AiResponse)> ExecuteScheduleCore(
        Schedule schedule,
        AgentKey agentKey,
        string? userId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var agent = agentFactory.CreateFromDefinition(
            agentKey,
            schedule.UserId ?? "scheduler",
            schedule.Agent);

        var thread = await agent.DeserializeThreadAsync(
            JsonSerializer.SerializeToElement(agentKey.ToString()),
            null,
            ct);

        var userMessage = new ChatMessage(ChatRole.User, schedule.Prompt);
        userMessage.SetSenderId(userId);
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
}