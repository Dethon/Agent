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
        [Description(
            "Wait strategy: 'networkidle' (default), 'domcontentloaded', 'load', 'selector', or 'stable'. Use 'stable' for JS-heavy SPAs")]
        string? waitStrategy = null,
        [Description(
            "CSS selector to wait for before extracting content. Use with waitStrategy='selector' or as additional wait condition")]
        string? waitSelector = null,
        [Description("Maximum time in ms to wait for page load (1000-120000, default: 30000)")]
        int waitTimeoutMs = 30000,
        [Description("Extra delay in ms after page load for dynamic content (0-10000, default: 1000)")]
        int extraDelayMs = 1000,
        [Description("Scroll page to trigger lazy-loaded content (default: false)")]
        bool scrollToLoad = false,
        [Description("Number of scroll steps for lazy loading (1-10, default: 3)")]
        int scrollSteps = 3,
        [Description("Wait until DOM stops changing - best for complex SPAs (default: false)")]
        bool waitForStability = false,
        CancellationToken ct = default)
    {
        try
        {
            var result = await RunAsync(url, selector, format, maxLength, includeLinks,
                waitStrategy, waitSelector, waitTimeoutMs, extraDelayMs, scrollToLoad, scrollSteps, waitForStability,
                ct);
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