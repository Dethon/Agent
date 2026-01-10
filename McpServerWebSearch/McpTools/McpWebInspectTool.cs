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
public class McpWebInspectTool(IWebBrowser browser, ILogger<McpWebInspectTool> logger)
    : WebInspectTool(browser)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        [Description("Inspection mode: 'structure' (default), 'search', 'forms', or 'interactive'")]
        string mode = "structure",
        [Description("Search query for 'search' mode (text or regex pattern)")]
        string? query = null,
        [Description("Treat query as regex pattern (default: false)")]
        bool regex = false,
        [Description("Maximum results for search mode (1-100, default: 20)")]
        int maxResults = 20,
        [Description("CSS selector to scope inspection to specific element")]
        string? selector = null,
        CancellationToken ct = default)
    {
        try
        {
            var sessionId = context.Server.StateKey;
            var result = await RunAsync(sessionId, mode, query, regex, maxResults, selector, ct);
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