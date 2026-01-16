namespace Domain.DTOs.WebChat;

public record StreamState(
    bool IsProcessing,
    IReadOnlyList<ChatStreamMessage> BufferedMessages,
    int CurrentMessageIndex,
    long LastSequenceNumber);