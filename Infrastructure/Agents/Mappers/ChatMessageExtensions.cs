using Domain.DTOs;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents.Mappers;

public static class ChatMessageExtensions
{
    extension(AiMessage message)
    {
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