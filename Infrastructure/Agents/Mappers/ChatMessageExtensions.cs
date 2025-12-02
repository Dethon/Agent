using Domain.DTOs;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Infrastructure.Agents.Mappers;

public static class ChatMessageExtensions
{
    extension(AiMessage message)
    {
        public ChatMessage ToChatMessage()
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

        public SamplingMessage ToSamplingMessage()
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
                Content =
                [
                    new TextContentBlock
                    {
                        Text = message.Content
                    }
                ]
            };
        }
    }
}