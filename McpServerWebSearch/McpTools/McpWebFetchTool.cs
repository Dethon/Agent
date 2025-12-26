using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Web;
using Infrastructure.Utils;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerWebSearch.McpTools;

[McpServerToolType]
public class McpWebFetchTool(IWebFetcher webFetcher, ILogger<McpWebFetchTool> logger)
    : WebFetchTool(webFetcher)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        [Description("The URL to fetch content from")]
        string url,
        [Description("CSS selector to target specific content (e.g., '.main-content', '#article')")]
        string? selector = null,
        [Description("Output format: 'text', 'markdown', or 'html' (default: 'markdown')")]
        string? format = null,
        [Description("Maximum characters to return (100-100000, default: 10000)")]
        int maxLength = 10000,
        [Description("Include hyperlinks in output (default: true)")]
        bool includeLinks = true,
        CancellationToken ct = default)
    {
        try
        {
            var result = await RunAsync(url, selector, format, maxLength, includeLinks, ct);
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