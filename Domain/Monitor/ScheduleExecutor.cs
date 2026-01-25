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
        var responses = scheduleChannel.Reader
            .ReadAllAsync(ct)
            .Select(schedule => ProcessScheduleAsync(schedule, ct))
            .Merge(ct);

        await messengerClient.ProcessResponseStreamAsync(responses, ct);
    }

    private async IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> ProcessScheduleAsync(
        Schedule schedule,
        [EnumeratorCancellation] CancellationToken ct)
    {
        AgentKey agentKey;

        try
        {
            agentKey = await messengerClient.CreateTopicIfNeededAsync(
                schedule.Target.ChatId,
                schedule.Target.ThreadId,
                schedule.Target.AgentId,
                "Scheduled task",
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error creating topic for schedule {ScheduleId}", schedule.Id);
            yield break;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Executing schedule {ScheduleId} for agent {AgentName} on thread {ThreadId}",
                schedule.Id,
                schedule.Agent.Name,
                agentKey.ThreadId);
        }

        await foreach (var item in ExecuteScheduleCore(schedule, agentKey, ct))
        {
            yield return item;
        }
    }

    private async IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> ExecuteScheduleCore(
        Schedule schedule,
        AgentKey agentKey,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var agent = agentFactory.CreateFromDefinition(
            agentKey,
            schedule.Target.UserId ?? "scheduler",
            schedule.Agent);

        var thread = await agent.DeserializeThreadAsync(
            JsonSerializer.SerializeToElement(agentKey.ToString()),
            null,
            ct);

        var userMessage = new ChatMessage(ChatRole.User, schedule.Prompt);

        await foreach (var (update, aiResponse) in agent
                           .RunStreamingAsync([userMessage], thread, cancellationToken: ct)
                           .WithErrorHandling(ct)
                           .ToUpdateAiResponsePairs()
                           .WithCancellation(ct))
        {
            yield return (agentKey, update, aiResponse);
        }

        yield return (agentKey, new AgentResponseUpdate { Contents = [new StreamCompleteContent()] }, null);

        if (schedule.CronExpression is null)
        {
            await store.DeleteAsync(schedule.Id, ct);
        }
    }
}