using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Web;

public class WebActionTool(IWebBrowser browser)
{
    protected const string Name = "WebAction";

    protected const string Description =
        """
        Interacts with an element on the current page by ref from WebSnapshot.
        Returns a diff showing only what changed — unless the action caused navigation,
        in which case the full new page snapshot is returned instead.
        Use WebSnapshot with a selector if you need more context after a diff.

        Actions requiring ref:
        - 'click': Click the element
        - 'type': Type character-by-character (triggers autocomplete). Set value to text.
        - 'fill': Set input value directly (no keystroke events). Set value to text.
        - 'select': Select native dropdown option. Set value to option text.
        - 'press': Press keyboard key. Set value to key name (Enter, Tab, Escape, ArrowDown).
        - 'clear': Clear input field.
        - 'hover': Hover over element (triggers tooltips, menus).
        - 'focus': Focus element (triggers datepickers, dropdowns that open on focus).
        - 'drag': Drag element to target. Set endRef to destination element ref.

        Actions NOT requiring ref (return full snapshot):
        - 'back': Navigate back in browser history.

        Workflow: WebSnapshot -> find ref -> WebAction(ref, action) -> read snapshot in response.
        For autocomplete: type partial text -> response shows options -> click option ref.

        force: only set this on a click that returned 'Timeout'. By default, clicks wait until the
        element is visible, stable, enabled, and not obscured by another element. Some pages layer
        a non-semantic <label>, decorative overlay, or floating placeholder over an input — those
        elements have no ARIA role, so they don't appear in the WebSnapshot but they intercept
        hit-testing and the click hangs until timeout. force=true skips those checks and dispatches
        the click directly on the target ref. Do NOT set force on the first attempt: the default
        checks are also what catches genuine "wrong ref / element gone / a real modal is in the
        way" bugs, and forcing them silently makes a click land on the wrong thing.
        """;

    protected async Task<WebActionResult> ExecuteAsync(
        string sessionId,
        string? @ref,
        string? action,
        string? value,
        string? endRef,
        bool waitForNavigation,
        bool force,
        CancellationToken ct)
    {
        var actionType = ParseActionType(action);

        var request = new WebActionRequest(
            SessionId: sessionId,
            Ref: @ref,
            Action: actionType,
            Value: value,
            EndRef: endRef,
            WaitForNavigation: waitForNavigation,
            Force: force);

        return await browser.ActionAsync(request, ct);
    }

    protected static JsonNode ToJson(WebActionResult result)
    {
        if (result.Status is not WebActionStatus.Success)
        {
            return new JsonObject
            {
                ["status"] = "error",
                ["sessionId"] = result.SessionId,
                ["errorType"] = result.Status.ToString(),
                ["url"] = result.Url,
                ["message"] = result.ErrorMessage
            };
        }

        var response = new JsonObject
        {
            ["status"] = "success",
            ["sessionId"] = result.SessionId,
            ["url"] = result.Url,
            ["navigationOccurred"] = result.NavigationOccurred
        };

        if (result.Snapshot is not null)
        {
            response["snapshot"] = result.Snapshot;
        }

        if (result.DialogMessage is not null)
        {
            response["dialogMessage"] = result.DialogMessage;
        }

        if (result.NavigationOccurred)
        {
            response["nextStep"] =
                $"Navigated to {result.Url}. The snapshot above shows interactive refs only, not page text. " +
                "If you need to read article/product/listing content, call WebBrowse with this URL. " +
                "If you only need to interact further, use the refs in the snapshot with WebAction.";
        }

        return response;
    }

    public static WebActionType ParseActionType(string? action)
    {
        if (string.IsNullOrEmpty(action))
        {
            return WebActionType.Click;
        }

        return action.ToLowerInvariant() switch
        {
            "click" => WebActionType.Click,
            "type" => WebActionType.Type,
            "fill" => WebActionType.Fill,
            "select" or "selectoption" => WebActionType.Select,
            "press" => WebActionType.Press,
            "clear" => WebActionType.Clear,
            "hover" => WebActionType.Hover,
            "focus" => WebActionType.Focus,
            "drag" => WebActionType.Drag,
            "back" => WebActionType.Back,
            _ => throw new ArgumentException($"Unknown action: {action}")
        };
    }
}
