using System.ComponentModel;
using McpChannelSignalR.Services;
using ModelContextProtocol.Server;

namespace McpChannelSignalR.McpTools;

[McpServerToolType]
public sealed class SubAgentAnnounceTool
{
    [McpServerTool(Name = "announce_subagent")]
    [Description("Announce that a subagent has started, so the UI can render a progress card")]
    public static async Task<string> McpRun(
        [Description("Conversation ID")] string conversationId,
        [Description("Subagent handle (unique identifier within this conversation)")] string handle,
        [Description("Subagent definition ID")] string subAgentId,
        IServiceProvider services)
    {
        var signalService = services.GetRequiredService<ISubAgentSignalService>();
        await signalService.AnnounceAsync(conversationId, handle, subAgentId);
        return "announced";
    }
}
