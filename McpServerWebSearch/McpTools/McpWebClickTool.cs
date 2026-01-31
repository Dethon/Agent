using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Web;
using Infrastructure.Utils;
using Infrastructure.Extensions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerWebSearch.McpTools;

[McpServerToolType]
public class McpWebClickTool(IWebBrowser browser)
    : WebClickTool(browser)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        [Description("CSS selector for the element to interact with (e.g., 'input[name=email]', 'button.submit')")]
        string selector,
        [Description(
            "Optional text to match within matching elements (filters results to elements containing this text)")]
        string? text = null,
        [Description("Action: 'click' (default), 'fill', 'clear', 'press', 'doubleclick', 'rightclick', 'hover'")]
        string? action = null,
        [Description("Text to type into input field (required for action='fill')")]
        string? inputValue = null,
        [Description("Keyboard key to press (for action='press'): 'Enter', 'Tab', 'Escape', 'Backspace', etc.")]
        string? key = null,
        [Description("Wait for page navigation after action (use when clicking links that load new pages)")]
        bool waitForNavigation = false,
        [Description("Max wait time in ms for navigation (1000-120000, default: 30000)")]
        int waitTimeoutMs = 30000,
        CancellationToken ct = default)
    {
        var sessionId = context.Server.StateKey;
        var result = await RunAsync(sessionId, selector, text, action, inputValue, key, waitForNavigation,
            waitTimeoutMs, ct);
        return ToolResponse.Create(result);
    }
}
