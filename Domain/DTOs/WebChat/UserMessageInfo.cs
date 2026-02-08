namespace Domain.DTOs.WebChat;

public record UserMessageInfo(string? SenderId, DateTimeOffset? Timestamp);