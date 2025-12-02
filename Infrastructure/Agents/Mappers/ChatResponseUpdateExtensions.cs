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
            var lastUpdate = updates.LastOrDefault();
            var text = string.Join("", updates
                .SelectMany(x => x.Contents)
                .OfType<TextContent>()
                .Select(x => x.Text));

            return new CreateMessageResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = text
                    }
                ],
                Model = "unknown",
                Role = lastUpdate?.Role == ChatRole.User ? Role.User : Role.Assistant,
                StopReason = "endTurn"
            };
        }
    }
}