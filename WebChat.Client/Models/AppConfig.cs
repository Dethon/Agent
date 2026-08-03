namespace WebChat.Client.Models;

public record AppConfig(string? AgentUrl, UserConfig[]? Users, string? VapidPublicKey = null);