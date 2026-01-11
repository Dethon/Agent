using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.Web;

public class WebBrowseTool(IWebBrowser browser)
{
    protected const string Name = "WebBrowse";

    protected const string Description =
        """
        Navigates to a URL in a persistent browser session.
        Automatically dismisses cookie consent, age gates, and other popups.
        Use this for multi-step browsing - the session persists between calls.
        Returns page content after modal dismissal.
        Use WebClick to interact with elements after navigating.
        """;

    protected async Task<JsonNode> RunAsync(
        string sessionId,
        string url,
        string? selector,
        string? format,
        int maxLength,
        bool includeLinks,
        bool useReadability,
        string? waitStrategy,
        string? waitSelector,
        int waitTimeoutMs,
        int extraDelayMs,
        bool scrollToLoad,
        int scrollSteps,
        bool waitForStability,
        bool dismissModals,
        CancellationToken ct)
    {
        var outputFormat = ParseFormat(format);
        var parsedWaitStrategy = ParseWaitStrategy(waitStrategy);

        var request = new BrowseRequest(
            SessionId: sessionId,
            Url: url,
            Selector: selector,
            Format: outputFormat,
            MaxLength: Math.Clamp(maxLength, 100, 100000),
            IncludeLinks: includeLinks,
            UseReadability: useReadability,
            WaitStrategy: parsedWaitStrategy,
            WaitSelector: waitSelector,
            WaitTimeoutMs: Math.Clamp(waitTimeoutMs, 1000, 120000),
            ExtraDelayMs: Math.Clamp(extraDelayMs, 0, 10000),
            ScrollToLoad: scrollToLoad,
            ScrollSteps: Math.Clamp(scrollSteps, 1, 10),
            WaitForStability: waitForStability,
            DismissModals: dismissModals
        );

        var result = await browser.NavigateAsync(request, ct);

        if (result.Status is BrowseStatus.Error or BrowseStatus.SessionNotFound)
        {
            return new JsonObject
            {
                ["status"] = "error",
                ["sessionId"] = result.SessionId,
                ["url"] = result.Url,
                ["message"] = result.ErrorMessage
            };
        }

        var response = new JsonObject
        {
            ["status"] = result.Status == BrowseStatus.Success ? "success" : "partial",
            ["sessionId"] = result.SessionId,
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

        if (result.DismissedModals is { Count: > 0 })
        {
            var modalsArray = new JsonArray();
            foreach (var modal in result.DismissedModals)
            {
                modalsArray.Add(new JsonObject
                {
                    ["type"] = modal.Type.ToString(),
                    ["selector"] = modal.Selector,
                    ["buttonText"] = modal.ButtonText
                });
            }

            response["dismissedModals"] = modalsArray;
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