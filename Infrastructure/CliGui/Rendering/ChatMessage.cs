namespace Infrastructure.CliGui.Rendering;

internal sealed record ChatMessage(
    string Sender,
    string Message,
    bool IsUser,
    bool IsToolCall,
    bool IsSystem,
    DateTime Timestamp);