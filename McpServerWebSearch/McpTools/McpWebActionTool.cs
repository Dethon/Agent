using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Web;
using Infrastructure.Extensions;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerWebSearch.McpTools;

[McpServerToolType]
public class McpWebActionTool(IWebBrowser browser)
    : WebActionTool(browser)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        [Description("Element ref from WebSnapshot (required for click, type, fill, select, press, clear, hover, drag)")]
        string? @ref = null,
        [Description("Action: 'click' (default), 'type', 'fill', 'select', 'press', 'clear', 'hover', 'drag', 'back', 'handleDialog'")]
        string? action = null,
        [Description("Value: text to type/fill, option text for select, key name for press (Enter/Tab/Escape/ArrowDown), 'accept'/'dismiss' for handleDialog")]
        string? value = null,
        [Description("Target ref for drag action (drag from ref to endRef)")]
        string? endRef = null,
        [Description("Wait for page navigation after action (for clicks that load new pages)")]
        bool waitForNavigation = false,
        CancellationToken ct = default)
    {
        var sessionId = context.Server.StateKey;
        var result = await ExecuteAsync(sessionId, @ref, action, value, endRef, waitForNavigation, ct);
        return ToolResponse.Create(ToJson(result));
    }
}
