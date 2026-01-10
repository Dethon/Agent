using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Web;
using Infrastructure.Utils;
using McpServerWebSearch.Extensions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerWebSearch.McpTools;

[McpServerToolType]
public class McpWebClickTool(IWebBrowser browser, ILogger<McpWebClickTool> logger)
    : WebClickTool(browser)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        [Description("CSS selector for the element to click (e.g., 'button.submit', 'a[href*=next]', '#load-more')")]
        string selector,
        [Description(
            "Optional text to match within matching elements (filters results to elements containing this text)")]
        string? text = null,
        [Description("Click action: 'click' (default), 'doubleclick', 'rightclick', or 'hover'")]
        string? action = null,
        [Description("Wait for page navigation after click (use when clicking links that load new pages)")]
        bool waitForNavigation = false,
        [Description("Max wait time in ms for navigation (1000-120000, default: 30000)")]
        int waitTimeoutMs = 30000,
        CancellationToken ct = default)
    {
        try
        {
            var sessionId = context.Server.StateKey;
            var result = await RunAsync(sessionId, selector, text, action, waitForNavigation, waitTimeoutMs, ct);
            return ToolResponse.Create(result);
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error))
            {
                logger.LogError(ex, "Error in {ToolName} tool", Name);
            }

            return ToolResponse.Create(ex);
        }
    }
}