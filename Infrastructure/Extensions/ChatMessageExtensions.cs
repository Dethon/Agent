using Domain.DTOs;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Infrastructure.Extensions;

public static class ChatMessageExtensions
{
    public static ChatMessage ToChatMessage(this AiMessage message)
    {
        var role = message.Role switch
        {
            AiMessageRole.User => ChatRole.User,
            AiMessageRole.System => ChatRole.System,
            AiMessageRole.Tool => ChatRole.Tool,
            _ => throw new NotSupportedException($"{message.Role} is not supported.")
        };
        return new ChatMessage(role, message.Content);
    }

    public static SamplingMessage ToSamplingMessage(this AiMessage message)
    {
        var role = message.Role switch
        {
            AiMessageRole.User => Role.User,
            AiMessageRole.System => Role.User,
            AiMessageRole.Assistant => Role.Assistant,
            _ => throw new NotSupportedException($"{message.Role} is not supported.")
        };
        return new SamplingMessage
        {
            Role = role,
            Content = new TextContentBlock
            {
                Text = message.Content
            }
        };
    }

    internal static CreateMessageResult ToCreateMessageResult(this ChatResponse chatResponse)
    {
        var lastMessage = chatResponse.Messages.LastOrDefault();
        return new CreateMessageResult
        {
            Content = new TextContentBlock
            {
                Text = lastMessage?.Text ?? string.Empty
            },
            Model = chatResponse.ModelId ?? "unknown",
            Role = lastMessage?.Role == ChatRole.User ? Role.User : Role.Assistant,
            StopReason = chatResponse.FinishReason == ChatFinishReason.Length ? "maxTokens" : "endTurn"
        };
    }
}