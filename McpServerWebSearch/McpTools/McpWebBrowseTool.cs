using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Web;
using Infrastructure.Extensions;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerWebSearch.McpTools;

[McpServerToolType]
public class McpWebBrowseTool(IWebBrowser browser)
    : WebBrowseTool(browser)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        [Description("The URL to navigate to")]
        string url,
        [Description("Maximum characters to return (100-100000, default: 10000)")]
        int maxLength = 10000,
        [Description("Character offset to start from (use 0 for beginning, increase to paginate)")]
        int offset = 0,
        [Description("CSS selector to extract specific elements (e.g. '.product-card', '#main'). Returns ALL matches.")]
        string? selector = null,
        [Description("Extract clean article content, stripping ads, navigation, sidebars")]
        bool useReadability = false,
        [Description("Scroll page to trigger lazy-loaded content")]
        bool scrollToLoad = false,
        [Description("Number of scroll steps for lazy loading (1-10, default: 3)")]
        int scrollSteps = 3,
        CancellationToken ct = default)
    {
        var sessionId = context.Server.RequireSessionId();
        var result = await RunAsync(sessionId, url, selector, maxLength, offset,
            useReadability, scrollToLoad, scrollSteps, ct);
        return ToolResponse.Create(result);
    }
}
