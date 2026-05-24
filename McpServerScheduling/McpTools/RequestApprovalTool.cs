using System.ComponentModel;
using Domain.DTOs;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpTools;

[McpServerToolType]
public sealed class RequestApprovalTool
{
    [McpServerTool(Name = "request_approval")]
    [Description("Request tool approval — scheduling auto-approves all tools")]
    public static string McpRun(
        [Description("Conversation ID")] string conversationId,
        [Description("Whether to ask the user (request) or just notify them (notify)")] ApprovalMode mode,
        [Description("JSON array of tool requests")] string requests)
        => mode == ApprovalMode.Notify ? "notified" : "approved";
}