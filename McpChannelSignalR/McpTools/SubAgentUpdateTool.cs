using System.ComponentModel;
using McpChannelSignalR.Services;
using ModelContextProtocol.Server;

namespace McpChannelSignalR.McpTools;

[McpServerToolType]
public sealed class SubAgentUpdateTool
{
    [McpServerTool(Name = "update_subagent")]
    [Description("Update the status of a running subagent so the UI card reflects the latest state")]
    public static async Task<string> McpRun(
        [Description("Conversation ID")] string conversationId,
        [Description("Subagent handle")] string handle,
        [Description("New status (e.g. Running, Completed, Failed, Cancelled)")] string status,
        IServiceProvider services)
    {
        var signalService = services.GetRequiredService<ISubAgentSignalService>();
        await signalService.UpdateAsync(conversationId, handle, status);
        return "updated";
    }
}
