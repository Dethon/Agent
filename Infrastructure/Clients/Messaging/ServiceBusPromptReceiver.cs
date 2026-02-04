using System.Threading.Channels;
using Domain.DTOs;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging;

public sealed class ServiceBusPromptReceiver(
    ServiceBusConversationMapper conversationMapper,
    ILogger<ServiceBusPromptReceiver> logger)
{
    private readonly Channel<ChatPrompt> _channel = Channel.CreateUnbounded<ChatPrompt>();
    private int _messageIdCounter;

    public IAsyncEnumerable<ChatPrompt> ReadPromptsAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);

    public async Task EnqueueAsync(ParsedServiceBusMessage message, CancellationToken ct)
    {
        var (chatId, threadId, _, _) = await conversationMapper.GetOrCreateMappingAsync(
            message.SourceId, message.AgentId, ct);

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
            "Enqueued prompt from Service Bus: sourceId={SourceId}, chatId={ChatId}",
            message.SourceId, chatId);

        await _channel.Writer.WriteAsync(prompt, ct);
    }

    public bool TryGetSourceId(long chatId, out string sourceId)
        => conversationMapper.TryGetSourceId(chatId, out sourceId);
}
