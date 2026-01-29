using Domain.DTOs.WebChat;
using WebChat.Client.Services.Streaming;

namespace WebChat.Client.State.Pipeline;

public interface IMessagePipeline
{
    string SubmitUserMessage(string topicId, string content, string? senderId);

    void AccumulateChunk(string topicId, string? messageId,
        string? content, string? reasoning, string? toolCalls);

    void FinalizeMessage(string topicId, string? messageId);

    void LoadHistory(string topicId, IEnumerable<ChatHistoryMessage> messages);

    void ResumeFromBuffer(BufferResumeResult result, string topicId, string? currentMessageId);

    void Reset(string topicId);

    bool WasSentByThisClient(string? correlationId);

    PipelineSnapshot GetSnapshot(string topicId);
}