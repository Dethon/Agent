using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Web;

public class WebFetchTool(IWebFetcher webFetcher)
{
    protected const string Name = "WebFetch";

    protected const string Description =
        """
        Fetches and extracts readable content from a URL.
        Use this after WebSearch to get full details from a promising search result.
        Can target specific content with CSS selectors and output as text, markdown, or HTML.
        For JavaScript-heavy sites (SPAs), use waitStrategy='stable' or provide a waitSelector.
        """;

    protected async Task<JsonNode> RunAsync(
        string url,
        string? selector,
        string? format,
        int maxLength,
        bool includeLinks,
        string? waitStrategy,
        string? waitSelector,
        int waitTimeoutMs,
        int extraDelayMs,
        bool scrollToLoad,
        int scrollSteps,
        bool waitForStability,
        CancellationToken ct)
    {
        var outputFormat = ParseFormat(format);
        var parsedWaitStrategy = ParseWaitStrategy(waitStrategy);

        var request = new WebFetchRequest(
            Url: url,
            Selector: selector,
            Format: outputFormat,
            MaxLength: Math.Clamp(maxLength, 100, 100000),
            IncludeLinks: includeLinks,
            WaitStrategy: parsedWaitStrategy,
            WaitSelector: waitSelector,
            WaitTimeoutMs: Math.Clamp(waitTimeoutMs, 1000, 120000),
            ExtraDelayMs: Math.Clamp(extraDelayMs, 0, 10000),
            ScrollToLoad: scrollToLoad,
            ScrollSteps: Math.Clamp(scrollSteps, 1, 10),
            WaitForStability: waitForStability
        );

        var result = await webFetcher.FetchAsync(request, ct);

        if (result.Status == WebFetchStatus.Error)
        {
            return new JsonObject
            {
                ["status"] = "error",
                ["url"] = result.Url,
                ["message"] = result.ErrorMessage
            };
        }

        var response = new JsonObject
        {
            ["status"] = result.Status == WebFetchStatus.Success ? "success" : "partial",
            ["url"] = result.Url,
            ["title"] = result.Title,
            ["content"] = result.Content,
            ["contentLength"] = result.ContentLength,
            ["truncated"] = result.Truncated
        };

        if (result.Metadata != null)
        {
            response["metadata"] = new JsonObject
            {
                ["description"] = result.Metadata.Description,
                ["author"] = result.Metadata.Author,
                ["datePublished"] = result.Metadata.DatePublished?.ToString("yyyy-MM-dd"),
                ["siteName"] = result.Metadata.SiteName
            };
        }

        if (result.Links is { Count: > 0 })
        {
            var linksArray = new JsonArray();
            foreach (var link in result.Links.Take(20))
            {
                linksArray.Add(new JsonObject
                {
                    ["text"] = link.Text,
                    ["url"] = link.Url
                });
            }

            response["links"] = linksArray;
        }

        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            response["message"] = result.ErrorMessage;
        }

        return response;
    }

    private static WebFetchOutputFormat ParseFormat(string? format)
    {
        if (string.IsNullOrEmpty(format))
        {
            return WebFetchOutputFormat.Markdown;
        }

        return format.ToLowerInvariant() switch
        {
            "text" => WebFetchOutputFormat.Text,
            "html" => WebFetchOutputFormat.Html,
            _ => WebFetchOutputFormat.Markdown
        };
    }

    private static WaitStrategy ParseWaitStrategy(string? strategy)
    {
        if (string.IsNullOrEmpty(strategy))
        {
            return WaitStrategy.NetworkIdle;
        }

        return strategy.ToLowerInvariant() switch
        {
            "domcontentloaded" => WaitStrategy.DomContentLoaded,
            "load" => WaitStrategy.Load,
            "selector" => WaitStrategy.Selector,
            "stable" => WaitStrategy.Stable,
            _ => WaitStrategy.NetworkIdle
        };
    }
}