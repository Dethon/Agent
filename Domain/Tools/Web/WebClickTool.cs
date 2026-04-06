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
        - 'fill': Set input value directly (requires inputValue). Fast but does NOT trigger autocomplete
        - 'type': Type into input character by character (requires inputValue). Triggers autocomplete/suggestions
        - 'clear': Clear an input field
        - 'press': Press a keyboard key (requires key: Enter, Tab, Escape, etc.)
        - 'selectOption': Select from a <select> dropdown (requires inputValue: option value or label)
        - 'setRange': Set a slider/range input value (requires inputValue: numeric value)
        - 'doubleclick': Double-click the element
        - 'rightclick': Right-click the element
        - 'hover': Hover over the element

        fill vs type:
        - Use 'fill' for standard inputs (name, email, password, search boxes without autocomplete)
        - Use 'type' when the input has autocomplete, typeahead, or live suggestions — fill sets the value
          but does NOT trigger the suggestion dropdown; type simulates real keystrokes that do

        The response adapts to what happened:
        - If a widget opened (calendar, dropdown, suggestions), you'll see the widget state and available options with selectors
        - If the page changed significantly, you'll see the new page content
        - Otherwise, you'll see the area around the element you interacted with

        Widget workflows:
        - Datepicker: click the date input → read calendar options → click desired date
        - Autocomplete: type partial text → read suggestions → click desired suggestion
        - Dropdown (native): use selectOption with the desired value
        - Dropdown (custom): click to open → read options → click desired option
        - Slider: use setRange with the desired numeric value

        Form workflow example:
        1. WebClick(selector="input[name='email']", action="fill", inputValue="user@example.com")
        2. WebClick(selector="input[name='city']", action="type", inputValue="New") → suggestions appear
        3. WebClick(selector=".suggestion-item:nth-child(1)") → "New York" selected
        4. WebClick(selector="select[name='country']", action="selectOption", inputValue="Spain")
        5. WebClick(selector="input[name='checkin']") → calendar opens, read dates
        6. WebClick(selector=".calendar-day[data-date='2026-04-15']") → date selected
        7. WebClick(selector="button[type='submit']", waitForNavigation=true)
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