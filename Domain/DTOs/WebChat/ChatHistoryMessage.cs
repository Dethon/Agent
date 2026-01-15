namespace Domain.DTOs.WebChat;

public record ChatHistoryMessage(
    string Role,
    string Content);