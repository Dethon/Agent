using System.Text.Json;
using Domain.DTOs;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents.Mappers;

public static class ChatResponseUpdateExtensions
{
    extension(IEnumerable<AgentRunResponseUpdate> updates)
    {
        public AiResponse ToAiResponse()
        {
            var contents = updates.SelectMany(x => x.Contents).ToArray();
            var text = string.Join("", contents.OfType<TextContent>().Select(x => x.Text));
            var toolCalls = string.Join("\n", contents.OfType<FunctionCallContent>()
                .Select(x => $"{x.Name}({JsonSerializer.Serialize(x.Arguments)})"));

            return new AiResponse
            {
                Content = text,
                ToolCalls = toolCalls
            };
        }

        public CreateMessageResult ToCreateMessageResult()
        {
            var enumerated = updates.ToArray();
            var response = enumerated.ToAiResponse();
            var role = enumerated.LastOrDefault()?.Role == ChatRole.User ? Role.User : Role.Assistant;

            return new CreateMessageResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = response.Content
                    }
                ],
                Model = "unknown",
                Role = role,
                StopReason = "endTurn"
            };
        }
    }
}