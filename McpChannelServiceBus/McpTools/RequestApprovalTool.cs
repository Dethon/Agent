using System.ComponentModel;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpChannelServiceBus.McpTools;

[McpServerToolType]
public sealed class RequestApprovalTool
{
    [McpServerTool(Name = "request_approval")]
    [Description("Request tool approval — ServiceBus auto-approves all tools")]
    public static string McpRun(
        [Description("Conversation ID (correlationId)")] string conversationId,
        [Description("Mode: request (interactive) or notify (fire-and-forget)")] string mode,
        [Description("JSON array of tool requests [{toolName, arguments}]")] string requests)
    {
        // Constructing the DTO ensures all required fields are present at compile time
        _ = new RequestApprovalParams
        {
            ConversationId = conversationId,
            Mode = mode,
            Requests = requests
        };

        // ServiceBus has no interactive user — auto-approve everything
        return mode == "notify" ? "notified" : "approved";
    }
}