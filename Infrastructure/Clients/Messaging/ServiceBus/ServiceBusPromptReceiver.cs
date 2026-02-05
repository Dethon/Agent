using System.Threading.Channels;
using Domain.DTOs;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging.ServiceBus;

public class ServiceBusPromptReceiver(
    ServiceBusConversationMapper conversationMapper,
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
        var (chatId, threadId, _, _) = await conversationMapper.GetOrCreateMappingAsync(
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

        await _channel.Writer.WriteAsync(prompt, ct);
    }

    public virtual bool TryGetCorrelationId(long chatId, out string correlationId)
    {
        return conversationMapper.TryGetCorrelationId(chatId, out correlationId);
    }
}