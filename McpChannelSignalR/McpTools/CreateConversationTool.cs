using System.ComponentModel;
using Domain.DTOs.Channel;
using McpChannelSignalR.Services;
using ModelContextProtocol.Server;

namespace McpChannelSignalR.McpTools;

[McpServerToolType]
public sealed class CreateConversationTool
{
    [McpServerTool(Name = "create_conversation")]
    [Description("Create a new conversation for agent-initiated messages")]
    public static async Task<string> McpRun(
        [Description("Agent identifier")] string agentId,
        [Description("Topic display name")] string topicName,
        [Description("User who initiated")] string sender,
        IServiceProvider services)
    {
        var p = new CreateConversationParams
        {
            AgentId = agentId,
            TopicName = topicName,
            Sender = sender
        };

        var sessionService = services.GetRequiredService<ISessionService>();
        var conversationId = await sessionService.CreateConversationAsync(p);
        return conversationId;
    }
}
