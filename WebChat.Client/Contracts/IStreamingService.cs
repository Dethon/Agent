using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;

namespace WebChat.Client.Contracts;

// Sending a message and driving a resumed stream. Whether a topic has a reply in flight is
// TopicStreams' to answer, not this.
public interface IStreamingService
{
    Task SendMessageAsync(StoredTopic topic, string message, string? correlationId = null);

    Task<bool> TryStartResumeStreamAsync(
        StreamLease lease, StoredTopic topic, ChatMessageModel streamingMessage, string startMessageId);
}