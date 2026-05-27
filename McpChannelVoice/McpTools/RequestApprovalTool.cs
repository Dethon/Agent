using System.ComponentModel;
using Domain.DTOs;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public sealed class RequestApprovalTool
{
    [McpServerTool(Name = ChannelProtocol.RequestApprovalTool)]
    [Description("Request user approval (placeholder, returns 'declined' or 'notified')")]
    public static Task<string> McpRun(
        [Description("Satellite ID owning the conversation")] string conversationId,
        [Description("Whether to ask the user or just notify them")] ApprovalMode mode,
        [Description("Tool requests to approve")] IReadOnlyList<ToolApprovalRequest> requests,
        IServiceProvider services)
    {
        var logger = services.GetService<ILogger<RequestApprovalTool>>();
        logger?.LogInformation(
            "request_approval (placeholder) conversation={ConversationId} mode={Mode} requests={Count}",
            conversationId, mode, requests.Count);

        return Task.FromResult(mode == ApprovalMode.Notify ? "notified" : "declined");
    }
}