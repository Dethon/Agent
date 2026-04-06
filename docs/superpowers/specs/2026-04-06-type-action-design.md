# Type Action for WebClick — Design Spec

**Date:** 2026-04-06
**Problem:** The `fill` action uses Playwright's `FillAsync()` which sets input values programmatically. This fires `input`/`change` events but NOT `keydown`/`keyup` events. Autocomplete/typeahead widgets (jQuery UI Autocomplete, custom implementations, etc.) listen for `keydown` to trigger suggestion searches. Since `keydown` never fires, autocomplete dropdowns never appear, and the agent cannot interact with them.

**Solution:** Add a `Type` action that clears the field first, then uses Playwright's `PressSequentiallyAsync()` to simulate real key presses one character at a time. Each character fires the full event chain (`keydown`, `keypress`, `keyup`, `input`), which triggers autocomplete widgets. A 50ms inter-keystroke delay allows debounce timers to function correctly.

---

## Changes

### 1. ClickAction Enum
Add `Type` to `ClickAction` in `Domain/Contracts/IWebBrowser.cs`, after `SetRange`.

### 2. ParseActionValue
Add `"type" => ClickAction.Type` to the switch in `Domain/Tools/Web/WebClickTool.cs`.

### 3. PerformClickAsync
Add to `Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs`:
```csharp
case ClickAction.Type:
    await locator.ClearAsync();
    await locator.PressSequentiallyAsync(request.InputValue ?? "", new() { Delay = 50 });
    break;
```
**Behavior:** Clears the field first (matching `fill` behavior), then types character by character with 50ms delay.

### 4. Tool Description
Update `Description` in `WebClickTool.cs` to add the `type` action and clarify `fill` vs `type`:
- `fill`: Fast, sets value directly. Use for standard form inputs without autocomplete.
- `type`: Simulates real typing character by character. Use when the input has autocomplete, typeahead, or suggestions. Slower than fill but triggers all keyboard events.

### 5. MCP Parameter Description
Update the action `[Description]` in `McpServerWebSearch/McpTools/McpWebClickTool.cs` to include `'type'`.

### 6. Tests
- Unit test: `type` parsing in `WebClickToolTests.cs`
- Unit test: `Type` action with SessionNotFound in `PlaywrightWebBrowserTests.cs`

---

## fill vs type — Agent Guidance

| Action | Mechanism | Events Fired | Use When |
|--------|-----------|-------------|----------|
| `fill` | `FillAsync()` — sets value directly | `input`, `change` | Standard form inputs, no autocomplete |
| `type` | `ClearAsync()` + `PressSequentiallyAsync()` | `keydown`, `keypress`, `keyup`, `input` per char | Inputs with autocomplete, typeahead, suggestions |

The WidgetDetector (from the smart click responses feature) will detect the autocomplete dropdown that appears after `type` and return structured widget content with selectable suggestions.
