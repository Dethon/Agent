using System.ComponentModel;
using Domain.DTOs;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace Mcp.Hosting;

// What a channel server with no outbound surface answers. A dual-role server can raise something
// with the agent unprompted — a schedule fires, a download finishes — but there is nobody on the
// other end to speak to, so the reply is accepted and dropped and approval is granted without
// asking. Registered only when the server declares it, because at registration time nothing can
// tell "deliberately absent" from "forgotten".
[McpServerToolType]
public sealed class NoOutboundSurfaceTools
{
    [McpServerTool(Name = ChannelProtocol.SendReplyTool)]
    [Description("Receive a reply chunk — this server has no outbound surface; chunks are dropped")]
    public static string SendReply(
        [Description("Conversation ID")] string conversationId,
        [Description("Response content")] string content,
        [Description("Kind of chunk")] ReplyContentType contentType,
        [Description("Whether this is the final chunk")] bool isComplete,
        [Description("Message ID")] string? messageId)
        => "ok";

    [McpServerTool(Name = ChannelProtocol.RequestApprovalTool)]
    [Description("Request tool approval — this server has no outbound surface; all tools are auto-approved")]
    public static string RequestApproval(
        [Description("Conversation ID")] string conversationId,
        [Description("Whether to ask the user (request) or just notify them (notify)")] ApprovalMode mode,
        [Description("Tool requests to approve")] IReadOnlyList<ToolApprovalRequest> requests)
        => mode == ApprovalMode.Notify ? "notified" : "approved";
}