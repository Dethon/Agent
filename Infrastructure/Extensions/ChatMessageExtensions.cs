using Domain.DTOs;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using DomainChatMessage = Domain.DTOs.ChatMessage;

namespace Infrastructure.Extensions;

public static class ChatMessageExtensions
{
    public static ChatMessage ToChatMessage(this DomainChatMessage message)
    {
        var role = message.Role switch
        {
            ChatMessageRole.User => ChatRole.User,
            ChatMessageRole.System => ChatRole.System,
            ChatMessageRole.Tool => ChatRole.Tool,
            _ => throw new NotSupportedException($"{message.Role} is not supported.")
        };
        return new ChatMessage(role, message.Content);
    }
}