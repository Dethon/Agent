using System.ComponentModel;
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
        [Description("Mode: request (interactive) or notify (fire-and-forget)")] string mode,
        [Description("JSON array of tool requests [{toolName, arguments}]")] string requests,
        IServiceProvider services)
    {
        var approvalService = services.GetRequiredService<IApprovalService>();

        if (mode == "notify")
        {
            await approvalService.NotifyAutoApprovedAsync(conversationId, requests);
            return "notified";
        }

        var result = await approvalService.RequestApprovalAsync(conversationId, requests);
        return result;
    }
}
