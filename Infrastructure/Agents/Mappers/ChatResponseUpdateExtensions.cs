using Domain.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents.Mappers;

public static class ChatResponseUpdateExtensions
{
    public static CreateMessageResult ToCreateMessageResult(this IEnumerable<AgentResponseUpdate> updates)
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