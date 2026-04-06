using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Web;

public class WebBrowseTool(IWebBrowser browser)
{
    protected const string Name = "WebBrowse";

    protected const string Description =
        """
        Navigates to a URL and returns page content as markdown.
        Maintains a persistent browser session (cookies, login state preserved).
        Automatically dismisses cookie popups, age gates, newsletter modals.

        Use selector to extract specific elements (e.g., selector=".product-card").
        Use maxLength/offset for pagination of long content.
        Use useReadability=true for clean article extraction (strips ads, nav, sidebars).
        Use scrollToLoad=true for pages with lazy-loaded content.

        Returns structured data (JSON-LD) when available on the page.

        For interacting with pages (clicking, filling forms), use WebSnapshot + WebAction.
        """;

    protected async Task<JsonNode> RunAsync(
        string sessionId,
        string url,
        string? selector,
        int maxLength,
        int offset,
        bool useReadability,
        bool scrollToLoad,
        int scrollSteps,
        CancellationToken ct)
    {
        maxLength = Math.Clamp(maxLength, 100, 100000);
        scrollSteps = Math.Clamp(scrollSteps, 1, 10);

        var request = new BrowseRequest(
            SessionId: sessionId,
            Url: url,
            Selector: selector,
            MaxLength: maxLength,
            Offset: offset,
            UseReadability: useReadability,
            ScrollToLoad: scrollToLoad,
            ScrollSteps: scrollSteps);

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
            ["status"] = result.Status == BrowseStatus.CaptchaRequired ? "captcha_required" : "success",
            ["sessionId"] = result.SessionId,
            ["url"] = result.Url,
            ["title"] = result.Title,
            ["content"] = result.Content,
            ["contentLength"] = result.ContentLength,
            ["truncated"] = result.Truncated
        };

        if (result.Metadata is not null)
        {
            response["metadata"] = new JsonObject
            {
                ["description"] = result.Metadata.Description,
                ["author"] = result.Metadata.Author,
                ["datePublished"] = result.Metadata.DatePublished?.ToString("yyyy-MM-dd"),
                ["siteName"] = result.Metadata.SiteName
            };
        }

        if (result.StructuredData is { Count: > 0 })
        {
            var sdArray = new JsonArray();
            foreach (var sd in result.StructuredData)
            {
                sdArray.Add(new JsonObject
                {
                    ["type"] = sd.Type,
                    ["data"] = sd.RawJson
                });
            }
            response["structuredData"] = sdArray;
        }

        if (result.DismissedModals is { Count: > 0 })
        {
            var modals = new JsonArray();
            foreach (var m in result.DismissedModals)
                modals.Add(m.Type.ToString());
            response["dismissedModals"] = modals;
        }

        return response;
    }
}
