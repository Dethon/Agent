namespace Domain.DTOs.WebChat;

public record PushSubscriptionDto(string Endpoint, string P256dh, string Auth);
