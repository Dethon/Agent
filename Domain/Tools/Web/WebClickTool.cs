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
        - 'selectOption': Select from a <select> dropdown (requires inputValue: option value or label)
        - 'setRange': Set a slider/range input value (requires inputValue: numeric value)
        - 'doubleclick': Double-click the element
        - 'rightclick': Right-click the element
        - 'hover': Hover over the element

        The response adapts to what happened:
        - If a widget opened (calendar, dropdown, suggestions), you'll see the widget state and available options with selectors
        - If the page changed significantly, you'll see the new page content
        - Otherwise, you'll see the area around the element you interacted with

        Widget workflows:
        - Datepicker: click the date input → read calendar options → click desired date
        - Autocomplete: fill with partial text → read suggestions → click desired suggestion
        - Dropdown (native): use selectOption with the desired value
        - Dropdown (custom): click to open → read options → click desired option
        - Slider: use setRange with the desired numeric value

        Form workflow example:
        1. WebClick(selector="input[name='email']", action="fill", inputValue="user@example.com")
        2. WebClick(selector="select[name='country']", action="selectOption", inputValue="Spain")
        3. WebClick(selector="input[name='checkin']") → calendar opens, read dates
        4. WebClick(selector=".calendar-day[data-date='2026-04-15']") → date selected
        5. WebClick(selector="button[type='submit']", waitForNavigation=true)
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
        var clickAction = ParseActionValue(action);

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

    protected internal static ClickAction ParseActionValue(string? action)
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
            "selectoption" => ClickAction.SelectOption,
            "setrange" => ClickAction.SetRange,
            "type" => ClickAction.Type,
            _ => ClickAction.Click
        };
    }
}