using System.Threading.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.WebChat;
using Infrastructure.Extensions;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging.ServiceBus;

public class ServiceBusPromptReceiver(
    ServiceBusConversationMapper conversationMapper,
    INotifier notifier,
    ILogger<ServiceBusPromptReceiver> logger)
{
    private readonly Channel<ChatPrompt> _channel = Channel.CreateUnbounded<ChatPrompt>();
    private int _messageIdCounter;

    public IAsyncEnumerable<ChatPrompt> ReadPromptsAsync(CancellationToken ct)
    {
        return _channel.Reader.ReadAllAsync(ct);
    }

    public async Task EnqueueAsync(ParsedServiceBusMessage message, CancellationToken ct)
    {
        var (chatId, threadId, topicId, _) = await conversationMapper.GetOrCreateMappingAsync(
            message.CorrelationId, message.AgentId, ct);

        var prompt = new ChatPrompt
        {
            Prompt = message.Prompt,
            ChatId = chatId,
            ThreadId = (int)threadId,
            MessageId = Interlocked.Increment(ref _messageIdCounter),
            Sender = message.Sender,
            AgentId = message.AgentId,
            Source = MessageSource.ServiceBus
        };

        logger.LogInformation(
            "Enqueued prompt from Service Bus: correlationId={CorrelationId}, chatId={ChatId}",
            message.CorrelationId, chatId);

        // Notify WebUI so the user message bubble appears in real-time
        await notifier.NotifyUserMessageAsync(
                new UserMessageNotification(topicId, message.Prompt, message.Sender, DateTimeOffset.UtcNow),
                ct)
            .SafeAwaitAsync(logger, "Failed to notify user message for topic {TopicId}", topicId);

        await _channel.Writer.WriteAsync(prompt, ct);
    }

    public virtual bool TryGetCorrelationId(long chatId, out string correlationId)
    {
        return conversationMapper.TryGetCorrelationId(chatId, out correlationId);
    }
}