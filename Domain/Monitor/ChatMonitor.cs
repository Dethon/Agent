using System.Threading.Channels;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

public class ChatMonitor(
    TaskQueue queue,
    IChatMessengerClient chatMessengerClient,
    Func<string, CancellationToken, Task<DisposableAgent>> agentFactory,
    ChannelResolver channelResolver,
    ILogger<ChatMonitor> logger)
{
    public async Task Monitor(CancellationToken cancellationToken)
    {
        try
        {
            var prompts = chatMessengerClient.ReadPrompts(1000, cancellationToken);
            await foreach (var prompt in prompts)
            {
                var agentKey = await CreateTopicIfNeeded(prompt, cancellationToken);
                var (channel, isNew) = channelResolver.Resolve(agentKey);

                if (prompt.Prompt.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
                {
                    channel.Writer.TryComplete();
                    channelResolver.Clean(agentKey);
                    continue;
                }

                await channel.Writer.WriteAsync(prompt, cancellationToken);
                if (isNew)
                {
                    await queue.QueueTask(ct => AgentTask(agentKey, channel.Reader, ct));
                }
            }
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error))
            {
                if (logger.IsEnabled(LogLevel.Error))
                {
                    logger.LogError(ex, "ChatMonitor exception: {exceptionMessage}", ex.Message);
                }
            }
        }
    }

    private async Task AgentTask(AgentKey agentKey, ChannelReader<ChatPrompt> reader, CancellationToken ct)
    {
        await using var agent = await agentFactory($"{agentKey.ChatId}-{agentKey.ThreadId}", ct);
        var thread = agent.GetNewThread();
        var aiResponses = reader
            .ReadAllAsync(ct)
            .Select(x => agent
                .RunStreamingAsync(x.Prompt, thread, cancellationToken: ct)
                .ToUpdateAiResponsePairs()
                .Where(y => y.Item2 is not null)
                .Select(y => y.Item2)
                .Cast<AiResponse>()
            )
            .Merge(ct);

        await chatMessengerClient.BlockWhile(agentKey.ChatId, agentKey.ThreadId, async c =>
        {
            await foreach (var aiResponse in aiResponses)
            {
                await SendResponse(agentKey, aiResponse, c);
            }
        }, ct);
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
            Message = prompt.Prompt,
            Bold = true
        };
        await chatMessengerClient.SendResponse(prompt.ChatId, responseMessage, threadId, cancellationToken);

        return new AgentKey(prompt.ChatId, threadId);
    }

    private async Task SendResponse(AgentKey agentKey, AiResponse response, CancellationToken ct)
    {
        try
        {
            var responseMessage = new ChatResponseMessage
            {
                Message = response.Content,
                CalledTools = response.ToolCalls
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