namespace Domain.DTOs.WebChat;

public record StreamState(
    bool IsProcessing,
    IReadOnlyList<ChatStreamMessage> BufferedMessages,
    string CurrentMessageId,
    string? CurrentPrompt);