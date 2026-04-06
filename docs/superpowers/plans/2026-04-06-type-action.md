# Type Action Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `Type` action to WebClick that simulates real key presses to trigger autocomplete/typeahead widgets.

**Architecture:** A single new `Type` value in the `ClickAction` enum, wired through parsing and execution. Uses Playwright's `PressSequentiallyAsync()` with a 50ms inter-keystroke delay after clearing the field. Description updates guide the agent on when to use `fill` vs `type`.

**Tech Stack:** .NET 10, Playwright, Shouldly (assertions)

---

## File Structure

| File | Responsibility |
|------|---------------|
| `Domain/Contracts/IWebBrowser.cs` | **MODIFY** — Add `Type` to `ClickAction` enum |
| `Domain/Tools/Web/WebClickTool.cs` | **MODIFY** — Add `"type"` case to `ParseActionValue`, update `Description` |
| `Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs` | **MODIFY** — Add `Type` case to `PerformClickAsync` |
| `McpServerWebSearch/McpTools/McpWebClickTool.cs` | **MODIFY** — Update action parameter `[Description]` |
| `Tests/Unit/Infrastructure/WebClickToolTests.cs` | **MODIFY** — Add `type` parsing tests |
| `Tests/Unit/Infrastructure/PlaywrightWebBrowserTests.cs` | **MODIFY** — Add `Type` action SessionNotFound test |

---

### Task 1: Add Type to ClickAction Enum and Parsing

**Files:**
- Modify: `Domain/Contracts/IWebBrowser.cs:193-204`
- Modify: `Domain/Tools/Web/WebClickTool.cs:95-114`
- Modify: `Tests/Unit/Infrastructure/WebClickToolTests.cs:8-17`

- [ ] **Step 1: Add test cases for type parsing**

In `Tests/Unit/Infrastructure/WebClickToolTests.cs`, add two new `[InlineData]` lines to the `ParseAction_NewActions_ReturnCorrectEnum` theory (after line 12):

```csharp
[InlineData("type", ClickAction.Type)]
[InlineData("Type", ClickAction.Type)]
```

The full theory becomes:
```csharp
[Theory]
[InlineData("selectoption", ClickAction.SelectOption)]
[InlineData("selectOption", ClickAction.SelectOption)]
[InlineData("setrange", ClickAction.SetRange)]
[InlineData("setRange", ClickAction.SetRange)]
[InlineData("type", ClickAction.Type)]
[InlineData("Type", ClickAction.Type)]
public void ParseAction_NewActions_ReturnCorrectEnum(string input, ClickAction expected)
{
    var result = TestableWebClickTool.TestParseAction(input);
    result.ShouldBe(expected);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~WebClickToolTests" --no-restore -v minimal`
Expected: Compilation error — `ClickAction.Type` does not exist.

- [ ] **Step 3: Add Type to the enum**

In `Domain/Contracts/IWebBrowser.cs`, add `Type` after `SetRange`:

```csharp
public enum ClickAction
{
    Click,
    DoubleClick,
    RightClick,
    Hover,
    Fill,
    Clear,
    Press,
    SelectOption,
    SetRange,
    Type
}
```

- [ ] **Step 4: Add type case to ParseActionValue**

In `Domain/Tools/Web/WebClickTool.cs`, add the `"type"` case to the switch in `ParseActionValue` (after line 111):

```csharp
"selectoption" => ClickAction.SelectOption,
"setrange" => ClickAction.SetRange,
"type" => ClickAction.Type,
_ => ClickAction.Click
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Tests --filter "FullyQualifiedName~WebClickToolTests" --no-restore -v minimal`
Expected: All tests PASS (15 tests — 6 new actions + 9 existing).

- [ ] **Step 6: Commit**

```bash
git add Domain/Contracts/IWebBrowser.cs Domain/Tools/Web/WebClickTool.cs Tests/Unit/Infrastructure/WebClickToolTests.cs
git commit -m "feat: add Type to ClickAction enum with parsing"
```

---

### Task 2: Wire Type Action into PerformClickAsync

**Files:**
- Modify: `Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs:590-610`
- Modify: `Tests/Unit/Infrastructure/PlaywrightWebBrowserTests.cs:108`

- [ ] **Step 1: Add unit test for Type action**

In `Tests/Unit/Infrastructure/PlaywrightWebBrowserTests.cs`, add before the closing `}` of the class (after line 108):

```csharp
[Fact]
public async Task ClickAsync_WithTypeAction_ReturnsSessionNotFound()
{
    var request = new ClickRequest(
        SessionId: "non-existent-session",
        Selector: "input.autocomplete",
        Action: ClickAction.Type,
        InputValue: "Odawara");
    var result = await _browser.ClickAsync(request);
    result.Status.ShouldBe(ClickStatus.SessionNotFound);
}
```

- [ ] **Step 2: Run the test to verify it passes**

Run: `dotnet test Tests --filter "FullyQualifiedName~PlaywrightWebBrowserTests" --no-restore -v minimal`
Expected: All tests PASS (SessionNotFound is returned before action execution).

- [ ] **Step 3: Add Type case to PerformClickAsync**

In `Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs`, add the `Type` case after the `SetRange` case (after line 605):

```csharp
case ClickAction.Type:
    await locator.ClearAsync();
    await locator.PressSequentiallyAsync(request.InputValue ?? "", new() { Delay = 50 });
    break;
```

- [ ] **Step 4: Run all tests to verify nothing broke**

Run: `dotnet test Tests --filter "FullyQualifiedName~PlaywrightWebBrowserTests" --no-restore -v minimal`
Expected: All tests PASS.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs Tests/Unit/Infrastructure/PlaywrightWebBrowserTests.cs
git commit -m "feat: wire Type action into PerformClickAsync"
```

---

### Task 3: Update Tool Descriptions

**Files:**
- Modify: `Domain/Tools/Web/WebClickTool.cs:10-44`
- Modify: `McpServerWebSearch/McpTools/McpWebClickTool.cs:24`

- [ ] **Step 1: Update Description in WebClickTool.cs**

Replace the `Description` constant (lines 10-44) with:

```csharp
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
```

- [ ] **Step 2: Update McpWebClickTool action parameter description**

In `McpServerWebSearch/McpTools/McpWebClickTool.cs`, replace line 24:

```csharp
[Description("Action: 'click' (default), 'fill', 'type', 'clear', 'press', 'selectOption', 'setRange', 'doubleclick', 'rightclick', 'hover'")]
```

Also replace line 26 (the inputValue description):

```csharp
[Description("Text to type into input field (required for action='fill' or 'type')")]
```

- [ ] **Step 3: Verify build succeeds**

Run: `dotnet build Tests --no-restore -v minimal`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add Domain/Tools/Web/WebClickTool.cs McpServerWebSearch/McpTools/McpWebClickTool.cs
git commit -m "feat: update WebClick description with type action and fill vs type guidance"
```

---

### Task 4: Full Test Suite Verification

**Files:** None (verification only)

- [ ] **Step 1: Run all unit tests**

Run: `dotnet test Tests --filter "FullyQualifiedName~Tests.Unit" --no-restore -v minimal`
Expected: All tests PASS.

- [ ] **Step 2: Build entire solution**

Run: `dotnet build --no-restore -v minimal`
Expected: Build succeeded with 0 errors.
