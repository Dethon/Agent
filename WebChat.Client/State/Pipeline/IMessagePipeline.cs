using Domain.DTOs.WebChat;
using WebChat.Client.Services.Streaming;

namespace WebChat.Client.State.Pipeline;

public interface IMessagePipeline
{
    /// <summary>User sends a message. Returns correlation ID for tracking.</summary>
    string SubmitUserMessage(string topicId, string content, string? senderId);

    /// <summary>Streaming chunk arrives from any source.</summary>
    void AccumulateChunk(string topicId, string? messageId,
        string? content, string? reasoning, string? toolCalls);

    /// <summary>Message complete (turn ended or stream finished).</summary>
    void FinalizeMessage(string topicId, string? messageId);

    /// <summary>Load history from server.</summary>
    void LoadHistory(string topicId, IEnumerable<ChatHistoryMessage> messages);

    /// <summary>Resume from buffered messages after reconnection.</summary>
    void ResumeFromBuffer(BufferResumeResult result, string topicId, string? currentMessageId);

    /// <summary>Reset pipeline state for topic (error or cancel).</summary>
    void Reset(string topicId);

    /// <summary>Check if a correlation ID was sent by this client.</summary>
    bool WasSentByThisClient(string? correlationId);

    /// <summary>Get debug snapshot of pipeline state.</summary>
    PipelineSnapshot GetSnapshot(string topicId);

    /// <summary>Lifecycle events for debugging.</summary>
    IObservable<MessageLifecycleEvent> LifecycleEvents { get; }
}
