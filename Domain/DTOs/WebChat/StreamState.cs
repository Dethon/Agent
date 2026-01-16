namespace Domain.DTOs.WebChat;

public record StreamState(
    bool IsProcessing,
    IReadOnlyList<ChatStreamMessage> BufferedMessages,
    int CurrentMessageIndex,
    string? CurrentPrompt);