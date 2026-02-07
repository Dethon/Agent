using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Web;

public class WebClickTool(IWebBrowser browser)
{
    protected const string Name = "WebClick";

    protected const string Description =
        """
        Interacts with an element on the current page in a browser session.
        Use after WebBrowse to interact with buttons, links, or form elements.

        Actions:
        - 'click' (default): Click the element
        - 'fill': Type text into an input field (requires inputValue)
        - 'clear': Clear an input field
        - 'press': Press a keyboard key (requires key: Enter, Tab, Escape, etc.)
        - 'doubleclick': Double-click the element
        - 'hover': Hover over the element

        Form workflow example:
        1. WebClick(selector="input[name='email']", action="fill", inputValue="user@example.com")
        2. WebClick(selector="input[name='password']", action="fill", inputValue="secret")
        3. WebClick(selector="button[type='submit']", waitForNavigation=true)
        """;

    protected async Task<JsonNode> RunAsync(
        string sessionId,
        string selector,
        string? text,
        string? action,
        string? inputValue,
        string? key,
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
            InputValue: inputValue,
            Key: key,
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
            "fill" => ClickAction.Fill,
            "clear" => ClickAction.Clear,
            "press" => ClickAction.Press,
            _ => ClickAction.Click
        };
    }
}