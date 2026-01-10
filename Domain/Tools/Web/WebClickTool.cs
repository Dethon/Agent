using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Web;

public class WebClickTool(IWebBrowser browser)
{
    protected const string Name = "WebClick";

    protected const string Description =
        """
        Clicks an element on the current page in a browser session.
        Use after WebBrowse to interact with buttons, links, or form elements.
        Supports CSS selectors and optional text matching.
        Can wait for page navigation if the click triggers a page load.
        Returns the new page content after clicking.
        """;

    protected async Task<JsonNode> RunAsync(
        string sessionId,
        string selector,
        string? text,
        string? action,
        bool waitForNavigation,
        int waitTimeoutMs,
        CancellationToken ct)
    {
        var clickAction = ParseAction(action);

        var request = new ClickRequest(
            SessionId: sessionId,
            Selector: selector,
            Action: clickAction,
            Text: text,
            WaitForNavigation: waitForNavigation,
            WaitTimeoutMs: Math.Clamp(waitTimeoutMs, 1000, 120000)
        );

        var result = await browser.ClickAsync(request, ct);

        if (result.Status != ClickStatus.Success)
        {
            return new JsonObject
            {
                ["status"] = "error",
                ["sessionId"] = result.SessionId,
                ["errorType"] = result.Status.ToString(),
                ["url"] = result.CurrentUrl,
                ["message"] = result.ErrorMessage
            };
        }

        return new JsonObject
        {
            ["status"] = "success",
            ["sessionId"] = result.SessionId,
            ["url"] = result.CurrentUrl,
            ["navigationOccurred"] = result.NavigationOccurred,
            ["content"] = result.Content,
            ["contentLength"] = result.ContentLength
        };
    }

    private static ClickAction ParseAction(string? action)
    {
        if (string.IsNullOrEmpty(action))
        {
            return ClickAction.Click;
        }

        return action.ToLowerInvariant() switch
        {
            "doubleclick" => ClickAction.DoubleClick,
            "rightclick" => ClickAction.RightClick,
            "hover" => ClickAction.Hover,
            _ => ClickAction.Click
        };
    }
}