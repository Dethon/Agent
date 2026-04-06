# Smart WebClick Responses for Interactive Widgets

## Problem

After `WebClick` actions, the tool returns the full page content as markdown (up to 10K chars). When the agent clicks a datepicker, types in an autocomplete, or opens a dropdown, the response is an undifferentiated wall of page text. The agent can't tell:

- That a calendar popup appeared with selectable dates
- That autocomplete suggestions are showing
- That a dropdown expanded with options
- Which elements it can interact with next

This forces multiple round-trips (click, then inspect, then click again) and often the agent gets stuck because it doesn't know what happened.

Additionally, the tools lack native support for:

- Selecting options from `<select>` elements (Playwright's `SelectOptionAsync`)
- Setting slider/range input values
- Guiding the agent on multi-step widget workflows

## Solution: Adaptive Post-Action Context

### Tiered Response Strategy

After performing any action, the tool evaluates what changed and returns the most useful context:

| Priority | Condition | Response |
|----------|-----------|----------|
| 1 | Widget detected near target | Structured widget summary + nearby actionable elements |
| 2 | Major page change (URL change or >50% DOM change) | Full page markdown (current behavior) |
| 3 | Minor/no change | Focused content from the area around the interacted element |

### Change Detection Algorithm

1. **Before action**: Snapshot the set of visible interactive elements (selectors + bounding boxes) and page URL
2. **Perform action**
3. **Wait** 500ms for dynamic content
4. **After action**: Scan for newly visible elements near the target element
5. **Decide tier**:
   - If new popup/overlay/listbox/calendar found near target → **Tier 1** (widget response)
   - Else if URL changed or page content hash changed significantly → **Tier 2** (full page)
   - Else → **Tier 3** (focused area)

### Widget Detection

After the action, scan for newly visible elements matching these patterns near the interacted element:

**Datepickers:**

- `[class*='calendar']`, `[class*='datepicker']`, `[class*='date-picker']`
- `[role='dialog']` or `[role='grid']` near date inputs
- `table` elements containing day-number cells
- `.flatpickr-calendar`, `.react-datepicker`, `.MuiPickersCalendar`, `.pikaday`

**Autocomplete / Combobox:**

- `[role='listbox']`, `[role='combobox']` with `[aria-expanded='true']`
- `[class*='autocomplete']`, `[class*='suggestions']`, `[class*='typeahead']`
- `[class*='dropdown-menu']` appearing after text input
- `ul` or `div` with `[class*='option']` children near text inputs

**Custom Dropdowns:**

- `[role='listbox']`, `[role='menu']`
- `[class*='select']`, `[class*='dropdown']` containers that became visible
- `[aria-expanded='true']` on the trigger element

**Sliders:**

- `input[type='range']` — report current value, min, max, step

### Widget Response Format

When a widget is detected, return structured content:

```
[Widget: datepicker]
Status: Calendar opened for "Check-in Date"
Current value: (none)
Visible month: April 2026

Available dates (selectable):
- "1" → selector: .calendar-day[data-date='2026-04-01']
- "2" → selector: .calendar-day[data-date='2026-04-02']
- ...

Navigation:
- Previous month → selector: .calendar-nav-prev
- Next month → selector: .calendar-nav-next

[Nearby actions]
- "Check-out Date" input → selector: input[name='checkout']
- "Search" button → selector: button.search-submit
```

```
[Widget: autocomplete]
Status: 3 suggestions for "New"
Input value: "New"

Suggestions:
- "New York, NY" → selector: .suggestion-item:nth-child(1)
- "New Jersey" → selector: .suggestion-item:nth-child(2)
- "Newark, NJ" → selector: .suggestion-item:nth-child(3)

[Nearby actions]
- Clear input → selector: .input-clear-btn
```

```
[Widget: dropdown]
Status: Dropdown opened for "Country"
Current value: "United States"

Options (showing 5 of 195):
- "Afghanistan" → selector: [role='option']:nth-child(1)
- "Albania" → selector: [role='option']:nth-child(2)
- ...

[Nearby actions]
- Search within dropdown → selector: .dropdown-search-input
```

```
[Widget: slider]
Status: Range input "Price"
Current value: 50
Range: 0 - 500 (step: 10)
```

### Focused Area Response (Tier 3)

When no widget is detected and no major page change occurred, extract content from the interacted element's parent container (walk up to a meaningful container: `form`, `section`, `article`, `div` with a role, or 2 levels up). This provides relevant context without the noise of the full page.

### New Click Actions

#### `selectOption`

Wraps Playwright's `SelectOptionAsync` for native `<select>` elements:

```
WebClick(selector="select[name='country']", action="selectOption", inputValue="US")
```

- Matches by value, label, or index
- Returns the selected option's text and value
- For custom (non-native) dropdowns, falls back to clicking the option with matching text

#### `setRange`

Sets slider/range input values via JavaScript:

```
WebClick(selector="input[name='price']", action="setRange", inputValue="250")
```

- Sets the `value` property directly
- Dispatches `input` and `change` events so frameworks react
- Returns the actual value set (clamped to min/max) and the slider's range

### Tool Description Update

Update the `WebClickTool.Description` to include widget workflow guidance:

```
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
- Dropdown (native): use selectOption action with the desired value
- Dropdown (custom): click to open → read options → click desired option
- Slider: use setRange action with the desired numeric value

Form workflow example:
1. WebClick(selector="input[name='email']", action="fill", inputValue="user@example.com")
2. WebClick(selector="select[name='country']", action="selectOption", inputValue="Spain")
3. WebClick(selector="input[name='checkin']") → calendar opens, read dates
4. WebClick(selector=".calendar-day[data-date='2026-04-15']") → date selected
5. WebClick(selector="button[type='submit']", waitForNavigation=true)
```

## Files to Modify

| File | Changes |
|------|---------|
| `Domain/Contracts/IWebBrowser.cs` | Add `SelectOption` and `SetRange` to `ClickAction` enum |
| `Domain/Tools/Web/WebClickTool.cs` | Update `Description` constant, add new action parsing |
| `Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs` | Implement adaptive response, widget detection, new actions, focused area extraction |
| `McpServerWebSearch/McpTools/McpWebClickTool.cs` | No changes needed (passes through) |

### New Files

| File | Purpose |
|------|---------|
| `Infrastructure/Clients/Browser/WidgetDetector.cs` | Widget detection heuristics — scans for newly visible popups/calendars/dropdowns near target element, extracts options and selectors |
| `Infrastructure/Clients/Browser/PostActionAnalyzer.cs` | Orchestrates the tiered response: calls WidgetDetector, measures page change, extracts focused area. Returns the appropriate content string |

## Testing Strategy

- Unit tests for `WidgetDetector` with HTML fixtures containing common datepicker/autocomplete/dropdown patterns
- Unit tests for `PostActionAnalyzer` tier selection logic
- Unit tests for `selectOption` and `setRange` action parsing
- Integration test with a test HTML page containing interactive widgets to verify end-to-end behavior

## Out of Scope

- CAPTCHA handling improvements (separate concern)
- Multi-tab support
- File upload widgets
- Drag-and-drop beyond sliders
