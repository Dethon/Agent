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
        public bool IsFinished()
        {
            var allContents = updates
                .SelectMany(x => x.Contents)
                .ToArray();

            // Message is finished when we have usage content indicating completion
            return allContents.Any(x => x is UsageContent);
        }

        public AiResponse ToAiResponse()
        {
            var enumeratedUpdates = updates.ToArray();
            var normalMessage = string.Join("", enumeratedUpdates
                .SelectMany(x => x.Contents)
                .Where(x => x is TextContent)
                .Cast<TextContent>()
                .Select(x => x.Text));

            var toolCallMessageContent = string.Join("\n", enumeratedUpdates
                .SelectMany(x => x.Contents)
                .Where(x => x is FunctionCallContent)
                .Cast<FunctionCallContent>()
                .Select(x => $"{x.Name}(\n{JsonSerializer.Serialize(x.Arguments)}\n)"));

            return new AiResponse
            {
                Content = normalMessage,
                ToolCalls = toolCallMessageContent
            };
        }

        public CreateMessageResult ToCreateMessageResult()
        {
            var enumeratedUpdates = updates.ToArray();
            var lastUpdate = enumeratedUpdates.LastOrDefault();
            var textContent = string.Join("", enumeratedUpdates
                .SelectMany(x => x.Contents)
                .Where(x => x is TextContent)
                .Cast<TextContent>()
                .Select(x => x.Text));

            return new CreateMessageResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = textContent
                    }
                ],
                Model = "unknown",
                Role = lastUpdate?.Role == ChatRole.User ? Role.User : Role.Assistant,
                StopReason = "endTurn"
            };
        }
    }
}