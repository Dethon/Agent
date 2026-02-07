using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Web;
using Infrastructure.Utils;
using Infrastructure.Extensions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerWebSearch.McpTools;

[McpServerToolType]
public class McpWebInspectTool(IWebBrowser browser)
    : WebInspectTool(browser)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        [Description("Inspection mode: 'structure' (default), 'search', 'forms', 'interactive', or 'tables'")]
        string mode = "structure",
        [Description(
            "Text to find in page for 'search' mode. Searches visible text content only, NOT CSS selectors. To find elements by selector, use WebBrowse instead.")]
        string? query = null,
        [Description("Treat query as regex pattern (default: false)")]
        bool regex = false,
        [Description("Maximum search results (1-100, default: 20)")]
        int maxResults = 20,
        [Description(
            "CSS selector to limit inspection scope to elements within this parent (e.g., '#main'). Does NOT search for this selector.")]
        string? selector = null,
        CancellationToken ct = default)
    {
        var sessionId = context.Server.StateKey;
        var result = await RunAsync(sessionId, mode, query, regex, maxResults, selector, ct);
        return ToolResponse.Create(result);
    }
}
