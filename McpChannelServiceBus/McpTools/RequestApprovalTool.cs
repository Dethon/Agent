using System.ComponentModel;
using Domain.DTOs;
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
        [Description("Whether to ask the user (request) or just notify them (notify)")] ApprovalMode mode,
        [Description("JSON array of tool requests [{toolName, arguments}]")] string requests)
    {
        _ = new RequestApprovalParams
        {
            ConversationId = conversationId,
            Mode = mode,
            Requests = requests
        };

        // ServiceBus has no interactive user — auto-approve everything
        return mode == ApprovalMode.Notify ? "notified" : "approved";
    }
}