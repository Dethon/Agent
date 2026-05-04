using System.ComponentModel;
using Domain.DTOs;
using Domain.DTOs.Channel;
using McpChannelSignalR.Services;
using ModelContextProtocol.Server;

namespace McpChannelSignalR.McpTools;

[McpServerToolType]
public sealed class RequestApprovalTool
{
    [McpServerTool(Name = "request_approval")]
    [Description("Request tool approval from user or notify about auto-approved tools")]
    public static async Task<string> McpRun(
        [Description("Conversation ID")] string conversationId,
        [Description("Whether to ask the user (request) or just notify them (notify)")] ApprovalMode mode,
        [Description("JSON array of tool requests [{toolName, arguments}]")] string requests,
        IServiceProvider services)
    {
        var p = new RequestApprovalParams
        {
            ConversationId = conversationId,
            Mode = mode,
            Requests = requests
        };

        var approvalService = services.GetRequiredService<IApprovalService>();

        if (p.Mode == ApprovalMode.Notify)
        {
            await approvalService.NotifyAutoApprovedAsync(p);
            return "notified";
        }

        var result = await approvalService.RequestApprovalAsync(p);
        return result;
    }
}
