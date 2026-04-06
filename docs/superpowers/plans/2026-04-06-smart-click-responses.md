# Smart WebClick Responses Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make WebClick return adaptive, context-aware responses so the LLM agent can effectively interact with datepickers, autocomplete, custom dropdowns, and sliders.

**Architecture:** After every click/fill/press action, a `PostActionAnalyzer` evaluates what changed on the page. If a widget opened (calendar, dropdown, suggestion list), it returns structured widget context with available options and selectors. If the page changed significantly, it returns full page markdown (current behavior). Otherwise it returns focused content from around the interacted element. Two new actions (`selectOption`, `setRange`) handle native `<select>` elements and range inputs. All widget detection logic lives in a dedicated `WidgetDetector` class that runs Playwright JS evaluation on the live page.

**Tech Stack:** .NET 10, Playwright, AngleSharp (HTML parsing), Shouldly (assertions)

---

## File Structure

| File | Responsibility |
|------|---------------|
| `Infrastructure/Clients/Browser/WidgetDetector.cs` | **NEW** — Runs JS on the live Playwright page to detect newly visible widgets (calendars, dropdowns, autocomplete, sliders) and extract their options/state |
| `Infrastructure/Clients/Browser/PostActionAnalyzer.cs` | **NEW** — Orchestrates the tiered response: calls WidgetDetector, measures page change magnitude, extracts focused area. Returns the content string for ClickResult |
| `Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs` | **MODIFY** — Replace inline content extraction in `ClickAsync` with `PostActionAnalyzer` call. Add `SelectOption` and `SetRange` to `PerformClickAsync`. Capture pre-action snapshot for change detection |
| `Domain/Contracts/IWebBrowser.cs` | **MODIFY** — Add `SelectOption` and `SetRange` to `ClickAction` enum |
| `Domain/Tools/Web/WebClickTool.cs` | **MODIFY** — Update `Description` constant with widget workflow guidance, add new action parsing |
| `Tests/Unit/Infrastructure/WidgetDetectorTests.cs` | **NEW** — Unit tests for widget detection heuristics using HTML fixtures |
| `Tests/Unit/Infrastructure/PostActionAnalyzerTests.cs` | **NEW** — Unit tests for tier selection and content formatting |
| `Tests/Unit/Infrastructure/WebClickToolTests.cs` | **NEW** — Unit tests for new action parsing |

---

### Task 1: Add SelectOption and SetRange to ClickAction Enum

**Files:**
- Modify: `Domain/Contracts/IWebBrowser.cs:193-202`
- Test: `Tests/Unit/Infrastructure/WebClickToolTests.cs`

- [ ] **Step 1: Write the failing test for new action parsing**

Create `Tests/Unit/Infrastructure/WebClickToolTests.cs`:

```csharp
using Domain.Contracts;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class WebClickToolTests
{
    [Theory]
    [InlineData("selectoption", ClickAction.SelectOption)]
    [InlineData("selectOption", ClickAction.SelectOption)]
    [InlineData("setrange", ClickAction.SetRange)]
    [InlineData("setRange", ClickAction.SetRange)]
    public void ParseAction_NewActions_ReturnCorrectEnum(string input, ClickAction expected)
    {
        var result = TestableWebClickTool.TestParseAction(input);
        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("click", ClickAction.Click)]
    [InlineData("fill", ClickAction.Fill)]
    [InlineData("clear", ClickAction.Clear)]
    [InlineData("press", ClickAction.Press)]
    [InlineData("doubleclick", ClickAction.DoubleClick)]
    [InlineData("rightclick", ClickAction.RightClick)]
    [InlineData("hover", ClickAction.Hover)]
    [InlineData(null, ClickAction.Click)]
    [InlineData("", ClickAction.Click)]
    public void ParseAction_ExistingActions_StillWork(string? input, ClickAction expected)
    {
        var result = TestableWebClickTool.TestParseAction(input);
        result.ShouldBe(expected);
    }
}

public class TestableWebClickTool : Domain.Tools.Web.WebClickTool
{
    public TestableWebClickTool() : base(null!) { }

    public static ClickAction TestParseAction(string? action)
    {
        // We need to make ParseAction accessible for testing
        // The base class has it as private static — we'll change it to protected internal
        return ParseActionValue(action);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~WebClickToolTests" --no-restore -v minimal`
Expected: Compilation errors — `SelectOption` and `SetRange` don't exist in `ClickAction`, `ParseActionValue` doesn't exist.

- [ ] **Step 3: Add new enum values**

In `Domain/Contracts/IWebBrowser.cs`, add to the `ClickAction` enum:

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
    SetRange
}
```

- [ ] **Step 4: Update WebClickTool parsing and make it testable**

In `Domain/Tools/Web/WebClickTool.cs`, change `ParseAction` to `protected internal static` and rename to `ParseActionValue`, then add the new cases:

```csharp
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
        _ => ClickAction.Click
    };
}
```

Also update the call site in `RunAsync` from `ParseAction(action)` to `ParseActionValue(action)`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Tests --filter "FullyQualifiedName~WebClickToolTests" --no-restore -v minimal`
Expected: All tests PASS.

- [ ] **Step 6: Commit**

```bash
git add Domain/Contracts/IWebBrowser.cs Domain/Tools/Web/WebClickTool.cs Tests/Unit/Infrastructure/WebClickToolTests.cs
git commit -m "feat: add SelectOption and SetRange click actions"
```

---

### Task 2: Implement WidgetDetector

**Files:**
- Create: `Infrastructure/Clients/Browser/WidgetDetector.cs`
- Create: `Tests/Unit/Infrastructure/WidgetDetectorTests.cs`

The WidgetDetector runs JavaScript on a live Playwright `IPage` to detect widgets that appeared after an action. It returns a structured `DetectedWidget` record describing the widget type, its options, and nearby actionable elements.

- [ ] **Step 1: Write the failing test for WidgetDetector**

Create `Tests/Unit/Infrastructure/WidgetDetectorTests.cs`:

```csharp
using Infrastructure.Clients.Browser;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class WidgetDetectorTests
{
    [Fact]
    public void FormatWidgetContent_Datepicker_FormatsCorrectly()
    {
        var widget = new DetectedWidget(
            WidgetType.Datepicker,
            Label: "Check-in Date",
            CurrentValue: null,
            Options:
            [
                new WidgetOption("15", ".day[data-date='2026-04-15']"),
                new WidgetOption("16", ".day[data-date='2026-04-16']"),
                new WidgetOption("17", ".day[data-date='2026-04-17']")
            ],
            Metadata: new Dictionary<string, string> { ["visibleMonth"] = "April 2026" },
            NearbyActions:
            [
                new NearbyAction("Check-out Date", "input[name='checkout']", "input"),
                new NearbyAction("Search", "button.search-submit", "button")
            ]);

        var content = WidgetDetector.FormatWidgetContent(widget);

        content.ShouldContain("[Widget: datepicker]");
        content.ShouldContain("Check-in Date");
        content.ShouldContain("\"15\"");
        content.ShouldContain(".day[data-date='2026-04-15']");
        content.ShouldContain("[Nearby actions]");
        content.ShouldContain("Check-out Date");
    }

    [Fact]
    public void FormatWidgetContent_Autocomplete_FormatsCorrectly()
    {
        var widget = new DetectedWidget(
            WidgetType.Autocomplete,
            Label: "City",
            CurrentValue: "New",
            Options:
            [
                new WidgetOption("New York, NY", ".suggestion-item:nth-child(1)"),
                new WidgetOption("New Jersey", ".suggestion-item:nth-child(2)")
            ],
            Metadata: null,
            NearbyActions: []);

        var content = WidgetDetector.FormatWidgetContent(widget);

        content.ShouldContain("[Widget: autocomplete]");
        content.ShouldContain("2 suggestions");
        content.ShouldContain("New York, NY");
        content.ShouldContain(".suggestion-item:nth-child(1)");
    }

    [Fact]
    public void FormatWidgetContent_Dropdown_FormatsCorrectly()
    {
        var widget = new DetectedWidget(
            WidgetType.Dropdown,
            Label: "Country",
            CurrentValue: "United States",
            Options:
            [
                new WidgetOption("Afghanistan", "[role='option']:nth-child(1)"),
                new WidgetOption("Albania", "[role='option']:nth-child(2)")
            ],
            Metadata: new Dictionary<string, string> { ["totalOptions"] = "195" },
            NearbyActions: []);

        var content = WidgetDetector.FormatWidgetContent(widget);

        content.ShouldContain("[Widget: dropdown]");
        content.ShouldContain("Country");
        content.ShouldContain("United States");
        content.ShouldContain("Afghanistan");
    }

    [Fact]
    public void FormatWidgetContent_Slider_FormatsCorrectly()
    {
        var widget = new DetectedWidget(
            WidgetType.Slider,
            Label: "Price",
            CurrentValue: "50",
            Options: [],
            Metadata: new Dictionary<string, string>
            {
                ["min"] = "0",
                ["max"] = "500",
                ["step"] = "10"
            },
            NearbyActions: []);

        var content = WidgetDetector.FormatWidgetContent(widget);

        content.ShouldContain("[Widget: slider]");
        content.ShouldContain("Price");
        content.ShouldContain("Current value: 50");
        content.ShouldContain("Range: 0 - 500");
    }

    [Fact]
    public void FormatWidgetContent_WithNoOptions_ShowsNoOptionsMessage()
    {
        var widget = new DetectedWidget(
            WidgetType.Dropdown,
            Label: "Empty",
            CurrentValue: null,
            Options: [],
            Metadata: null,
            NearbyActions: []);

        var content = WidgetDetector.FormatWidgetContent(widget);

        content.ShouldContain("[Widget: dropdown]");
        content.ShouldContain("No options visible");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~WidgetDetectorTests" --no-restore -v minimal`
Expected: Compilation errors — `WidgetDetector`, `DetectedWidget`, etc. don't exist.

- [ ] **Step 3: Create WidgetDetector with DTOs and FormatWidgetContent**

Create `Infrastructure/Clients/Browser/WidgetDetector.cs`:

```csharp
using System.Text;
using Microsoft.Playwright;

namespace Infrastructure.Clients.Browser;

public enum WidgetType
{
    Datepicker,
    Autocomplete,
    Dropdown,
    Slider
}

public record DetectedWidget(
    WidgetType Type,
    string? Label,
    string? CurrentValue,
    IReadOnlyList<WidgetOption> Options,
    IReadOnlyDictionary<string, string>? Metadata,
    IReadOnlyList<NearbyAction> NearbyActions);

public record WidgetOption(string Text, string Selector);

public record NearbyAction(string Text, string Selector, string ElementType);

public static class WidgetDetector
{
    private const int MaxOptionsToShow = 15;

    public static async Task<DetectedWidget?> DetectWidgetAsync(IPage page, string targetSelector)
    {
        // Run JS to detect visible widgets near the target element
        var widgetInfo = await page.EvaluateAsync<WidgetJsResult?>("""
            (targetSelector) => {
                const target = document.querySelector(targetSelector);
                if (!target) return null;

                // Get target's bounding rect for proximity checks
                const targetRect = target.getBoundingClientRect();
                const proximity = 500; // pixels

                function isNear(el) {
                    const rect = el.getBoundingClientRect();
                    return rect.width > 0 && rect.height > 0 &&
                        Math.abs(rect.top - targetRect.top) < proximity &&
                        Math.abs(rect.left - targetRect.left) < proximity * 2;
                }

                function isVisible(el) {
                    const style = window.getComputedStyle(el);
                    return style.display !== 'none' && style.visibility !== 'hidden' && style.opacity !== '0'
                        && el.getBoundingClientRect().height > 0;
                }

                function getSelector(el) {
                    if (el.id) return '#' + CSS.escape(el.id);
                    if (el.getAttribute('name')) return el.tagName.toLowerCase() + '[name="' + el.getAttribute('name') + '"]';
                    const classes = el.className && typeof el.className === 'string'
                        ? '.' + el.className.trim().split(/\s+/).slice(0, 2).map(c => CSS.escape(c)).join('.')
                        : '';
                    const tag = el.tagName.toLowerCase();
                    const parent = el.parentElement;
                    if (!parent) return tag + classes;
                    const siblings = Array.from(parent.children).filter(c => c.tagName === el.tagName);
                    if (siblings.length === 1) return tag + classes;
                    const idx = siblings.indexOf(el) + 1;
                    return tag + classes + ':nth-of-type(' + idx + ')';
                }

                function getLabel(el) {
                    // Check aria-label, associated label, placeholder, preceding text
                    if (el.getAttribute('aria-label')) return el.getAttribute('aria-label');
                    if (el.id) {
                        const label = document.querySelector('label[for="' + el.id + '"]');
                        if (label) return label.textContent.trim();
                    }
                    const parentLabel = el.closest('label');
                    if (parentLabel) return parentLabel.textContent.replace(el.textContent || '', '').trim();
                    if (el.placeholder) return el.placeholder;
                    return null;
                }

                // 1. Check for datepicker
                const datepickerSelectors = [
                    '[class*="calendar"]', '[class*="datepicker"]', '[class*="date-picker"]',
                    '[role="dialog"][class*="date"]', '[role="grid"]',
                    '.flatpickr-calendar', '.react-datepicker', '.MuiPickersCalendar-root',
                    '.pikaday', '.ui-datepicker'
                ];
                for (const sel of datepickerSelectors) {
                    const els = document.querySelectorAll(sel);
                    for (const el of els) {
                        if (!isVisible(el) || !isNear(el)) continue;
                        // Extract selectable dates
                        const days = Array.from(el.querySelectorAll(
                            '[class*="day"]:not([class*="disabled"]):not([class*="outside"]):not([aria-disabled="true"]), ' +
                            'td[data-date]:not(.disabled), td:not(.disabled) a'
                        )).filter(d => isVisible(d) && d.textContent.trim().length <= 2);
                        const options = days.slice(0, 31).map(d => ({
                            text: d.textContent.trim(),
                            selector: getSelector(d)
                        }));
                        // Try to find month/year header
                        const header = el.querySelector(
                            '[class*="month"], [class*="title"], [class*="header"], .flatpickr-current-month'
                        );
                        const metadata = {};
                        if (header) metadata.visibleMonth = header.textContent.trim();
                        // Navigation
                        const prevBtn = el.querySelector('[class*="prev"], [aria-label*="prev"], [class*="left"]');
                        const nextBtn = el.querySelector('[class*="next"], [aria-label*="next"], [class*="right"]');
                        if (prevBtn) options.unshift({ text: '← Previous month', selector: getSelector(prevBtn) });
                        if (nextBtn) options.push({ text: 'Next month →', selector: getSelector(nextBtn) });

                        return {
                            type: 'datepicker',
                            label: getLabel(target),
                            currentValue: target.value || null,
                            options: options,
                            metadata: metadata
                        };
                    }
                }

                // 2. Check for autocomplete/suggestion list
                const autocompleteSelectors = [
                    '[role="listbox"]', '[class*="autocomplete"]', '[class*="suggestions"]',
                    '[class*="typeahead"]', '[class*="dropdown-menu"]',
                    '[class*="combobox"] [role="option"]', 'ul[class*="option"]',
                    '[class*="select-menu"]', '[class*="results"]'
                ];
                const expandedTrigger = target.getAttribute('aria-expanded') === 'true' ||
                    target.closest('[aria-expanded="true"]');
                if (expandedTrigger || target.tagName === 'INPUT') {
                    for (const sel of autocompleteSelectors) {
                        const els = document.querySelectorAll(sel);
                        for (const el of els) {
                            if (!isVisible(el) || !isNear(el)) continue;
                            const items = Array.from(el.querySelectorAll(
                                '[role="option"], li, [class*="item"], [class*="option"]'
                            )).filter(i => isVisible(i) && i.textContent.trim().length > 0);
                            if (items.length === 0) continue;
                            const options = items.slice(0, 20).map(i => ({
                                text: i.textContent.trim().substring(0, 100),
                                selector: getSelector(i)
                            }));
                            return {
                                type: 'autocomplete',
                                label: getLabel(target),
                                currentValue: target.value || null,
                                options: options,
                                metadata: null
                            };
                        }
                    }
                }

                // 3. Check for custom dropdown (not native <select>)
                const dropdownSelectors = [
                    '[role="listbox"]', '[role="menu"]',
                    '[class*="dropdown"][class*="open"]', '[class*="dropdown"][class*="show"]',
                    '[class*="select"][class*="open"]', '[class*="select"][class*="show"]',
                    '[class*="menu"][class*="open"]', '[class*="menu"][class*="show"]'
                ];
                for (const sel of dropdownSelectors) {
                    const els = document.querySelectorAll(sel);
                    for (const el of els) {
                        if (!isVisible(el) || !isNear(el)) continue;
                        const items = Array.from(el.querySelectorAll(
                            '[role="option"], [role="menuitem"], li, [class*="item"], [class*="option"]'
                        )).filter(i => isVisible(i) && i.textContent.trim().length > 0);
                        if (items.length === 0) continue;
                        const totalOptions = items.length;
                        const options = items.slice(0, 20).map(i => ({
                            text: i.textContent.trim().substring(0, 100),
                            selector: getSelector(i)
                        }));
                        const metadata = { totalOptions: String(totalOptions) };
                        // Find current selection
                        const selected = items.find(i =>
                            i.getAttribute('aria-selected') === 'true' || i.classList.contains('selected'));
                        return {
                            type: 'dropdown',
                            label: getLabel(target),
                            currentValue: selected ? selected.textContent.trim() : (target.textContent.trim() || null),
                            options: options,
                            metadata: metadata
                        };
                    }
                }

                // 4. Check for slider/range input
                if (target.type === 'range' || target.getAttribute('role') === 'slider') {
                    return {
                        type: 'slider',
                        label: getLabel(target),
                        currentValue: target.value,
                        options: [],
                        metadata: {
                            min: target.min || '0',
                            max: target.max || '100',
                            step: target.step || '1'
                        }
                    };
                }

                return null;
            }
            """, targetSelector);

        if (widgetInfo == null) return null;

        var type = widgetInfo.Type switch
        {
            "datepicker" => WidgetType.Datepicker,
            "autocomplete" => WidgetType.Autocomplete,
            "dropdown" => WidgetType.Dropdown,
            "slider" => WidgetType.Slider,
            _ => WidgetType.Dropdown
        };

        var options = widgetInfo.Options?
            .Select(o => new WidgetOption(o.Text, o.Selector))
            .ToList() ?? [];

        var metadata = widgetInfo.Metadata?
            .Where(kvp => kvp.Value != null)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!);

        // Get nearby actionable elements
        var nearbyActions = await GetNearbyActionsAsync(page, targetSelector);

        return new DetectedWidget(type, widgetInfo.Label, widgetInfo.CurrentValue, options,
            metadata?.Count > 0 ? metadata : null, nearbyActions);
    }

    private static async Task<IReadOnlyList<NearbyAction>> GetNearbyActionsAsync(IPage page, string targetSelector)
    {
        var actions = await page.EvaluateAsync<List<NearbyActionJs>?>("""
            (targetSelector) => {
                const target = document.querySelector(targetSelector);
                if (!target) return [];

                const targetRect = target.getBoundingClientRect();
                const proximity = 400;

                function isNear(el) {
                    const rect = el.getBoundingClientRect();
                    return rect.width > 0 && rect.height > 0 &&
                        Math.abs(rect.top - targetRect.top) < proximity &&
                        Math.abs(rect.left - targetRect.left) < proximity * 2;
                }

                function isVisible(el) {
                    const style = window.getComputedStyle(el);
                    return style.display !== 'none' && style.visibility !== 'hidden' && style.opacity !== '0'
                        && el.getBoundingClientRect().height > 0;
                }

                function getSelector(el) {
                    if (el.id) return '#' + CSS.escape(el.id);
                    if (el.getAttribute('name')) return el.tagName.toLowerCase() + '[name="' + el.getAttribute('name') + '"]';
                    const classes = el.className && typeof el.className === 'string'
                        ? '.' + el.className.trim().split(/\s+/).slice(0, 2).map(c => CSS.escape(c)).join('.')
                        : '';
                    const tag = el.tagName.toLowerCase();
                    const parent = el.parentElement;
                    if (!parent) return tag + classes;
                    const siblings = Array.from(parent.children).filter(c => c.tagName === el.tagName);
                    if (siblings.length === 1) return tag + classes;
                    const idx = siblings.indexOf(el) + 1;
                    return tag + classes + ':nth-of-type(' + idx + ')';
                }

                function getLabel(el) {
                    if (el.getAttribute('aria-label')) return el.getAttribute('aria-label');
                    if (el.id) {
                        const label = document.querySelector('label[for="' + el.id + '"]');
                        if (label) return label.textContent.trim();
                    }
                    if (el.placeholder) return el.placeholder;
                    if (el.textContent.trim().length < 50) return el.textContent.trim();
                    return el.tagName.toLowerCase();
                }

                const nearby = [];
                const actionableSelectors = 'input:not([type="hidden"]), select, textarea, button, a[href]';
                const elements = document.querySelectorAll(actionableSelectors);

                for (const el of elements) {
                    if (el === target || !isVisible(el) || !isNear(el)) continue;
                    const label = getLabel(el);
                    if (!label) continue;
                    nearby.push({
                        text: label.substring(0, 80),
                        selector: getSelector(el),
                        elementType: el.tagName.toLowerCase()
                    });
                    if (nearby.length >= 8) break;
                }

                return nearby;
            }
            """, targetSelector);

        return actions?
            .Select(a => new NearbyAction(a.Text, a.Selector, a.ElementType))
            .ToList() ?? [];
    }

    public static string FormatWidgetContent(DetectedWidget widget)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"[Widget: {widget.Type.ToString().ToLowerInvariant()}]");

        switch (widget.Type)
        {
            case WidgetType.Datepicker:
                FormatDatepicker(sb, widget);
                break;
            case WidgetType.Autocomplete:
                FormatAutocomplete(sb, widget);
                break;
            case WidgetType.Dropdown:
                FormatDropdown(sb, widget);
                break;
            case WidgetType.Slider:
                FormatSlider(sb, widget);
                break;
        }

        if (widget.NearbyActions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[Nearby actions]");
            foreach (var action in widget.NearbyActions)
            {
                sb.AppendLine($"- \"{action.Text}\" ({action.ElementType}) → selector: {action.Selector}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static void FormatDatepicker(StringBuilder sb, DetectedWidget widget)
    {
        var label = widget.Label ?? "date input";
        sb.AppendLine($"Status: Calendar opened for \"{label}\"");
        sb.AppendLine($"Current value: {widget.CurrentValue ?? "(none)"}");

        if (widget.Metadata?.TryGetValue("visibleMonth", out var month) == true)
        {
            sb.AppendLine($"Visible month: {month}");
        }

        sb.AppendLine();
        if (widget.Options.Count == 0)
        {
            sb.AppendLine("No selectable dates visible.");
        }
        else
        {
            sb.AppendLine("Available dates/navigation:");
            foreach (var opt in widget.Options.Take(MaxOptionsToShow))
            {
                sb.AppendLine($"- \"{opt.Text}\" → selector: {opt.Selector}");
            }

            if (widget.Options.Count > MaxOptionsToShow)
            {
                sb.AppendLine($"  ... and {widget.Options.Count - MaxOptionsToShow} more");
            }
        }
    }

    private static void FormatAutocomplete(StringBuilder sb, DetectedWidget widget)
    {
        var label = widget.Label ?? "input";
        sb.AppendLine($"Status: {widget.Options.Count} suggestions for \"{label}\"");
        sb.AppendLine($"Input value: \"{widget.CurrentValue ?? ""}\"");

        sb.AppendLine();
        if (widget.Options.Count == 0)
        {
            sb.AppendLine("No suggestions visible.");
        }
        else
        {
            sb.AppendLine("Suggestions:");
            foreach (var opt in widget.Options.Take(MaxOptionsToShow))
            {
                sb.AppendLine($"- \"{opt.Text}\" → selector: {opt.Selector}");
            }

            if (widget.Options.Count > MaxOptionsToShow)
            {
                sb.AppendLine($"  ... and {widget.Options.Count - MaxOptionsToShow} more");
            }
        }
    }

    private static void FormatDropdown(StringBuilder sb, DetectedWidget widget)
    {
        var label = widget.Label ?? "dropdown";
        sb.AppendLine($"Status: Dropdown opened for \"{label}\"");
        sb.AppendLine($"Current value: {widget.CurrentValue ?? "(none)"}");

        var total = widget.Metadata?.TryGetValue("totalOptions", out var t) == true ? t : null;

        sb.AppendLine();
        if (widget.Options.Count == 0)
        {
            sb.AppendLine("No options visible.");
        }
        else
        {
            var showing = total != null ? $" (showing {widget.Options.Count} of {total})" : "";
            sb.AppendLine($"Options{showing}:");
            foreach (var opt in widget.Options.Take(MaxOptionsToShow))
            {
                sb.AppendLine($"- \"{opt.Text}\" → selector: {opt.Selector}");
            }

            if (widget.Options.Count > MaxOptionsToShow)
            {
                sb.AppendLine($"  ... and {widget.Options.Count - MaxOptionsToShow} more");
            }
        }
    }

    private static void FormatSlider(StringBuilder sb, DetectedWidget widget)
    {
        var label = widget.Label ?? "slider";
        sb.AppendLine($"Status: Range input \"{label}\"");
        sb.AppendLine($"Current value: {widget.CurrentValue ?? "unknown"}");

        var min = widget.Metadata?.TryGetValue("min", out var mn) == true ? mn : "0";
        var max = widget.Metadata?.TryGetValue("max", out var mx) == true ? mx : "100";
        var step = widget.Metadata?.TryGetValue("step", out var st) == true ? st : "1";

        sb.AppendLine($"Range: {min} - {max} (step: {step})");
    }

    // JS interop DTOs — internal, used only for deserialization from Playwright EvaluateAsync
    private record WidgetJsResult
    {
        public string Type { get; init; } = "";
        public string? Label { get; init; }
        public string? CurrentValue { get; init; }
        public List<WidgetOptionJs>? Options { get; init; }
        public Dictionary<string, string?>? Metadata { get; init; }
    }

    private record WidgetOptionJs
    {
        public string Text { get; init; } = "";
        public string Selector { get; init; } = "";
    }

    private record NearbyActionJs
    {
        public string Text { get; init; } = "";
        public string Selector { get; init; } = "";
        public string ElementType { get; init; } = "";
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests --filter "FullyQualifiedName~WidgetDetectorTests" --no-restore -v minimal`
Expected: All 5 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Clients/Browser/WidgetDetector.cs Tests/Unit/Infrastructure/WidgetDetectorTests.cs
git commit -m "feat: add WidgetDetector with widget formatting"
```

---

### Task 3: Implement PostActionAnalyzer

**Files:**
- Create: `Infrastructure/Clients/Browser/PostActionAnalyzer.cs`
- Create: `Tests/Unit/Infrastructure/PostActionAnalyzerTests.cs`

The PostActionAnalyzer orchestrates the tiered response strategy: widget detection → major page change → focused area.

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/Infrastructure/PostActionAnalyzerTests.cs`:

```csharp
using Infrastructure.Clients.Browser;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class PostActionAnalyzerTests
{
    [Fact]
    public void DetermineResponseTier_WithWidget_ReturnsWidgetTier()
    {
        var tier = PostActionAnalyzer.DetermineResponseTier(
            widgetDetected: true,
            urlChanged: false,
            contentChangeFraction: 0.05);

        tier.ShouldBe(ResponseTier.Widget);
    }

    [Fact]
    public void DetermineResponseTier_WithUrlChange_ReturnsFullPageTier()
    {
        var tier = PostActionAnalyzer.DetermineResponseTier(
            widgetDetected: false,
            urlChanged: true,
            contentChangeFraction: 0.8);

        tier.ShouldBe(ResponseTier.FullPage);
    }

    [Fact]
    public void DetermineResponseTier_WithMajorContentChange_ReturnsFullPageTier()
    {
        var tier = PostActionAnalyzer.DetermineResponseTier(
            widgetDetected: false,
            urlChanged: false,
            contentChangeFraction: 0.55);

        tier.ShouldBe(ResponseTier.FullPage);
    }

    [Fact]
    public void DetermineResponseTier_WithMinorChange_ReturnsFocusedTier()
    {
        var tier = PostActionAnalyzer.DetermineResponseTier(
            widgetDetected: false,
            urlChanged: false,
            contentChangeFraction: 0.1);

        tier.ShouldBe(ResponseTier.Focused);
    }

    [Fact]
    public void DetermineResponseTier_WidgetTakesPriorityOverUrlChange()
    {
        var tier = PostActionAnalyzer.DetermineResponseTier(
            widgetDetected: true,
            urlChanged: true,
            contentChangeFraction: 0.9);

        tier.ShouldBe(ResponseTier.Widget);
    }

    [Fact]
    public void ComputeContentChangeFraction_IdenticalContent_ReturnsZero()
    {
        var fraction = PostActionAnalyzer.ComputeContentChangeFraction(
            "Hello world", "Hello world");

        fraction.ShouldBe(0.0);
    }

    [Fact]
    public void ComputeContentChangeFraction_CompletelyDifferent_ReturnsOne()
    {
        var fraction = PostActionAnalyzer.ComputeContentChangeFraction(
            "aaaaaaaaaa", "bbbbbbbbbb");

        fraction.ShouldBe(1.0);
    }

    [Fact]
    public void ComputeContentChangeFraction_EmptyBefore_ReturnsOne()
    {
        var fraction = PostActionAnalyzer.ComputeContentChangeFraction(
            "", "some content");

        fraction.ShouldBe(1.0);
    }

    [Fact]
    public void ComputeContentChangeFraction_SmallChange_ReturnsSmallValue()
    {
        var before = "The quick brown fox jumps over the lazy dog. Some more text here to make it longer.";
        var after = "The quick brown fox jumps over the lazy cat. Some more text here to make it longer.";

        var fraction = PostActionAnalyzer.ComputeContentChangeFraction(before, after);

        fraction.ShouldBeGreaterThan(0.0);
        fraction.ShouldBeLessThan(0.2);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~PostActionAnalyzerTests" --no-restore -v minimal`
Expected: Compilation errors — `PostActionAnalyzer`, `ResponseTier` don't exist.

- [ ] **Step 3: Implement PostActionAnalyzer**

Create `Infrastructure/Clients/Browser/PostActionAnalyzer.cs`:

```csharp
using Infrastructure.HtmlProcessing;
using Domain.DTOs;
using Microsoft.Playwright;

namespace Infrastructure.Clients.Browser;

public enum ResponseTier
{
    Widget,
    FullPage,
    Focused
}

public static class PostActionAnalyzer
{
    private const double MajorChangeThreshold = 0.5;
    private const int MaxContentLength = 10000;
    private const int FocusedAreaMaxLength = 5000;

    public static ResponseTier DetermineResponseTier(bool widgetDetected, bool urlChanged, double contentChangeFraction)
    {
        if (widgetDetected) return ResponseTier.Widget;
        if (urlChanged || contentChangeFraction > MajorChangeThreshold) return ResponseTier.FullPage;
        return ResponseTier.Focused;
    }

    public static double ComputeContentChangeFraction(string before, string after)
    {
        if (string.IsNullOrEmpty(before)) return string.IsNullOrEmpty(after) ? 0.0 : 1.0;
        if (string.IsNullOrEmpty(after)) return 1.0;
        if (before == after) return 0.0;

        // Use a simple length-weighted character difference heuristic
        // Compare fixed-size samples from start, middle, and end
        var maxLen = Math.Max(before.Length, after.Length);
        var sampleSize = Math.Min(200, Math.Min(before.Length, after.Length));

        var diffCount = 0;
        var totalSampled = 0;

        // Sample from start
        var startSample = Math.Min(sampleSize, Math.Min(before.Length, after.Length));
        for (var i = 0; i < startSample; i++)
        {
            if (before[i] != after[i]) diffCount++;
            totalSampled++;
        }

        // Sample from end
        var endSample = Math.Min(sampleSize, Math.Min(before.Length, after.Length));
        for (var i = 0; i < endSample; i++)
        {
            if (before[before.Length - 1 - i] != after[after.Length - 1 - i]) diffCount++;
            totalSampled++;
        }

        // Length difference contributes to change
        var lengthDiff = Math.Abs(before.Length - after.Length);
        var lengthFraction = (double)lengthDiff / maxLen;

        var sampleFraction = totalSampled > 0 ? (double)diffCount / totalSampled : 0.0;

        // Weighted combination: character differences + length change
        var combined = (sampleFraction * 0.7) + (lengthFraction * 0.3);
        return Math.Min(1.0, combined);
    }

    public static async Task<string> AnalyzeAsync(
        IPage page,
        string targetSelector,
        string urlBefore,
        string contentBefore,
        CancellationToken ct = default)
    {
        var urlAfter = page.Url;
        var urlChanged = urlAfter != urlBefore;

        // Try widget detection first
        var widget = await WidgetDetector.DetectWidgetAsync(page, targetSelector);

        // Get page content for change comparison
        var htmlAfter = await page.ContentAsync();
        var contentAfter = HtmlConverter.Convert(htmlAfter, WebFetchOutputFormat.Markdown);

        var changeFraction = ComputeContentChangeFraction(contentBefore, contentAfter);
        var tier = DetermineResponseTier(widget != null, urlChanged, changeFraction);

        return tier switch
        {
            ResponseTier.Widget => WidgetDetector.FormatWidgetContent(widget!),
            ResponseTier.FullPage => TruncateContent(contentAfter, MaxContentLength),
            ResponseTier.Focused => await ExtractFocusedAreaAsync(page, targetSelector, contentAfter),
            _ => TruncateContent(contentAfter, MaxContentLength)
        };
    }

    public static string GetContentSnapshot(string html)
    {
        return HtmlConverter.Convert(html, WebFetchOutputFormat.Markdown);
    }

    private static async Task<string> ExtractFocusedAreaAsync(IPage page, string targetSelector, string fullContent)
    {
        // Try to extract content from the area around the interacted element
        var focusedHtml = await page.EvaluateAsync<string?>("""
            (selector) => {
                const target = document.querySelector(selector);
                if (!target) return null;

                // Walk up to find a meaningful container
                const containerTags = ['FORM', 'SECTION', 'ARTICLE', 'MAIN', 'DIALOG'];
                let container = target.parentElement;
                let depth = 0;
                while (container && depth < 4) {
                    if (containerTags.includes(container.tagName) ||
                        container.getAttribute('role') === 'dialog' ||
                        container.getAttribute('role') === 'form' ||
                        container.getAttribute('role') === 'region') {
                        break;
                    }
                    container = container.parentElement;
                    depth++;
                }

                // If no meaningful container found, use 2 levels up from target
                if (!container || depth >= 4) {
                    container = target.parentElement?.parentElement || target.parentElement || target;
                }

                return container ? container.outerHTML : null;
            }
            """, targetSelector);

        if (!string.IsNullOrEmpty(focusedHtml))
        {
            var focusedContent = HtmlConverter.Convert(focusedHtml, WebFetchOutputFormat.Markdown);
            if (focusedContent.Length > 50)
            {
                return TruncateContent(focusedContent, FocusedAreaMaxLength);
            }
        }

        // Fallback to full content if focused extraction yielded nothing useful
        return TruncateContent(fullContent, MaxContentLength);
    }

    private static string TruncateContent(string content, int maxLength)
    {
        if (content.Length <= maxLength) return content;
        return content[..maxLength] + "\n\n... (content truncated)";
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests --filter "FullyQualifiedName~PostActionAnalyzerTests" --no-restore -v minimal`
Expected: All 9 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Clients/Browser/PostActionAnalyzer.cs Tests/Unit/Infrastructure/PostActionAnalyzerTests.cs
git commit -m "feat: add PostActionAnalyzer for tiered click responses"
```

---

### Task 4: Wire PostActionAnalyzer into PlaywrightWebBrowser.ClickAsync

**Files:**
- Modify: `Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs:197-331`

This is the integration point. We replace the current "dump full page markdown" behavior with the adaptive PostActionAnalyzer.

- [ ] **Step 1: Write the failing test for new actions in PerformClickAsync**

Add to `Tests/Unit/Infrastructure/PlaywrightWebBrowserTests.cs`:

```csharp
[Fact]
public async Task ClickAsync_WithSelectOptionAction_ReturnsSessionNotFound()
{
    // Verifies the new action is at least parseable end-to-end
    var request = new ClickRequest(
        SessionId: "non-existent-session",
        Selector: "select",
        Action: ClickAction.SelectOption,
        InputValue: "option1");
    var result = await _browser.ClickAsync(request);
    result.Status.ShouldBe(ClickStatus.SessionNotFound);
}

[Fact]
public async Task ClickAsync_WithSetRangeAction_ReturnsSessionNotFound()
{
    var request = new ClickRequest(
        SessionId: "non-existent-session",
        Selector: "input[type='range']",
        Action: ClickAction.SetRange,
        InputValue: "50");
    var result = await _browser.ClickAsync(request);
    result.Status.ShouldBe(ClickStatus.SessionNotFound);
}
```

- [ ] **Step 2: Run the tests to verify they pass** (they should already pass since SessionNotFound is returned before action execution)

Run: `dotnet test Tests --filter "FullyQualifiedName~PlaywrightWebBrowserTests" --no-restore -v minimal`
Expected: All tests PASS (including the new ones).

- [ ] **Step 3: Modify PerformClickAsync to handle new actions**

In `Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs`, update the `PerformClickAsync` method to handle `SelectOption` and `SetRange`:

```csharp
private static async Task PerformClickAsync(ILocator locator, ClickRequest request)
{
    switch (request.Action)
    {
        case ClickAction.DoubleClick:
            await locator.DblClickAsync();
            break;
        case ClickAction.RightClick:
            await locator.ClickAsync(new LocatorClickOptions { Button = MouseButton.Right });
            break;
        case ClickAction.Hover:
            await locator.HoverAsync();
            break;
        case ClickAction.Fill:
            await locator.FillAsync(request.InputValue ?? "");
            break;
        case ClickAction.Clear:
            await locator.ClearAsync();
            break;
        case ClickAction.Press:
            await locator.PressAsync(request.Key ?? "Enter");
            break;
        case ClickAction.SelectOption:
            await locator.SelectOptionAsync(request.InputValue ?? "");
            break;
        case ClickAction.SetRange:
            await locator.EvaluateAsync("""
                (el, value) => {
                    el.value = value;
                    el.dispatchEvent(new Event('input', { bubbles: true }));
                    el.dispatchEvent(new Event('change', { bubbles: true }));
                }
                """, request.InputValue ?? "0");
            break;
        default:
            await locator.ClickAsync();
            break;
    }
}
```

- [ ] **Step 4: Modify ClickAsync to use PostActionAnalyzer**

Replace the content extraction section in `ClickAsync` (lines 270-293 approximately). The full updated method body from the `try` block after `PerformClickAsync`:

```csharp
// Perform click action
var urlBefore = page.Url;
var htmlBefore = await page.ContentAsync();
var contentBefore = PostActionAnalyzer.GetContentSnapshot(htmlBefore);

await PerformClickAsync(locator, request);

if (request.WaitForNavigation)
{
    try
    {
        await page.WaitForURLAsync(
            url => url != urlBefore,
            new PageWaitForURLOptions { Timeout = request.WaitTimeoutMs });
    }
    catch (TimeoutException)
    {
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
            new PageWaitForLoadStateOptions { Timeout = request.WaitTimeoutMs });
    }
}
else
{
    await Task.Delay(500, ct);
}

_sessions.UpdateCurrentUrl(request.SessionId, page.Url);

// Adaptive content extraction
var content = await PostActionAnalyzer.AnalyzeAsync(
    page, request.Selector, urlBefore, contentBefore, ct);

return new ClickResult(
    request.SessionId,
    ClickStatus.Success,
    page.Url,
    content,
    content.Length,
    null,
    page.Url != originalUrl
);
```

- [ ] **Step 5: Verify all existing tests still pass**

Run: `dotnet test Tests --filter "FullyQualifiedName~PlaywrightWebBrowserTests" --no-restore -v minimal`
Expected: All tests PASS.

- [ ] **Step 6: Commit**

```bash
git add Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs Tests/Unit/Infrastructure/PlaywrightWebBrowserTests.cs
git commit -m "feat: wire PostActionAnalyzer into ClickAsync for adaptive responses"
```

---

### Task 5: Update WebClickTool Description

**Files:**
- Modify: `Domain/Tools/Web/WebClickTool.cs:10-27`

- [ ] **Step 1: Update the Description constant**

In `Domain/Tools/Web/WebClickTool.cs`, replace the `Description` constant:

```csharp
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
```

- [ ] **Step 2: Update McpWebClickTool action parameter description**

In `McpServerWebSearch/McpTools/McpWebClickTool.cs`, update the action parameter's `[Description]`:

```csharp
[Description("Action: 'click' (default), 'fill', 'clear', 'press', 'selectOption', 'setRange', 'doubleclick', 'rightclick', 'hover'")]
string? action = null,
```

- [ ] **Step 3: Verify build succeeds**

Run: `dotnet build Tests --no-restore -v minimal`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add Domain/Tools/Web/WebClickTool.cs McpServerWebSearch/McpTools/McpWebClickTool.cs
git commit -m "feat: update WebClick description with widget workflows and new actions"
```

---

### Task 6: Integration Tests for Widget Detection and New Actions

**Files:**
- Modify: `Tests/Integration/Clients/PlaywrightWebBrowserTests.cs`

These tests verify the full pipeline against a real browser. They use existing sites with interactive elements.

- [ ] **Step 1: Write integration test for selectOption on a native select**

Add to `Tests/Integration/Clients/PlaywrightWebBrowserTests.cs`:

```csharp
[SkippableFact]
public async Task ClickAsync_SelectOptionAction_SelectsFromDropdown()
{
    Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

    var sessionId = GetUniqueSessionId();
    try
    {
        // Navigate to a page with a native <select> element
        var browseRequest = new BrowseRequest(
            SessionId: sessionId,
            Url: "https://httpbin.org/forms/post",
            MaxLength: 5000,
            WaitStrategy: WaitStrategy.DomContentLoaded,
            WaitTimeoutMs: 8000,
            DismissModals: false);
        var browseResult = await fixture.Browser.NavigateAsync(browseRequest);
        browseResult.Status.ShouldBeOneOf(BrowseStatus.Success, BrowseStatus.Partial);

        // Select an option from the custegg dropdown (if present) or any select
        var inspectRequest = new InspectRequest(sessionId, InspectMode.Forms);
        var inspectResult = await fixture.Browser.InspectAsync(inspectRequest);

        // Find any select field
        var selectField = inspectResult.Forms?
            .SelectMany(f => f.Fields)
            .FirstOrDefault(f => f.Type == "select");

        if (selectField != null)
        {
            var selectRequest = new ClickRequest(
                SessionId: sessionId,
                Selector: selectField.Selector,
                Action: ClickAction.SelectOption,
                InputValue: selectField.Name ?? "1", // Try selecting by index
                WaitTimeoutMs: 3000);
            var selectResult = await fixture.Browser.ClickAsync(selectRequest);

            testOutputHelper.WriteLine($"SelectOption status: {selectResult.Status}");
            testOutputHelper.WriteLine($"Content preview: {selectResult.Content?[..Math.Min(500, selectResult.Content?.Length ?? 0)]}");
            selectResult.Status.ShouldBe(ClickStatus.Success);
        }
        else
        {
            testOutputHelper.WriteLine("No select element found on page, skipping");
        }
    }
    finally
    {
        await fixture.Browser.CloseSessionAsync(sessionId);
    }
}
```

- [ ] **Step 2: Write integration test for adaptive response on form fill**

```csharp
[SkippableFact]
public async Task ClickAsync_FillAction_ReturnsFocusedContextNotFullPage()
{
    Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

    var sessionId = GetUniqueSessionId();
    try
    {
        // Navigate to DuckDuckGo
        var browseRequest = new BrowseRequest(
            SessionId: sessionId,
            Url: "https://duckduckgo.com",
            MaxLength: 5000,
            DismissModals: false,
            WaitStrategy: WaitStrategy.NetworkIdle,
            WaitTimeoutMs: 10000);
        var browseResult = await fixture.Browser.NavigateAsync(browseRequest);
        browseResult.Status.ShouldBeOneOf(BrowseStatus.Success, BrowseStatus.Partial);

        // Fill search input — should get focused/widget response, not full page
        var fillRequest = new ClickRequest(
            SessionId: sessionId,
            Selector: "input[name='q']",
            Action: ClickAction.Fill,
            InputValue: "playwright",
            WaitTimeoutMs: 3000);
        var fillResult = await fixture.Browser.ClickAsync(fillRequest);

        fillResult.Status.ShouldBe(ClickStatus.Success);
        fillResult.Content.ShouldNotBeNull();

        testOutputHelper.WriteLine($"Fill response length: {fillResult.ContentLength}");
        testOutputHelper.WriteLine($"Content preview: {fillResult.Content[..Math.Min(1000, fillResult.Content.Length)]}");

        // The response should either be a widget (autocomplete) or focused area — not the full page
        // Full page from DuckDuckGo is typically >5000 chars, focused should be smaller
        // OR it should contain widget markers
        var isWidget = fillResult.Content.Contains("[Widget:");
        var isFocused = fillResult.ContentLength < 6000;
        (isWidget || isFocused).ShouldBeTrue(
            $"Expected widget or focused response, got {fillResult.ContentLength} chars");
    }
    finally
    {
        await fixture.Browser.CloseSessionAsync(sessionId);
    }
}
```

- [ ] **Step 3: Run the integration tests**

Run: `dotnet test Tests --filter "FullyQualifiedName~PlaywrightWebBrowserTests.ClickAsync_SelectOption" --no-restore -v minimal`
Run: `dotnet test Tests --filter "FullyQualifiedName~PlaywrightWebBrowserTests.ClickAsync_FillAction_ReturnsFocusedContext" --no-restore -v minimal`
Expected: Tests PASS (or skip if no Camoufox available).

- [ ] **Step 4: Run all existing integration tests to verify no regressions**

Run: `dotnet test Tests --filter "FullyQualifiedName~PlaywrightWebBrowserTests" --no-restore -v minimal`
Expected: All tests PASS.

- [ ] **Step 5: Commit**

```bash
git add Tests/Integration/Clients/PlaywrightWebBrowserTests.cs
git commit -m "test: add integration tests for selectOption and adaptive responses"
```

---

### Task 7: Full Test Suite Verification

**Files:** None (verification only)

- [ ] **Step 1: Run all unit tests**

Run: `dotnet test Tests --filter "FullyQualifiedName~Tests.Unit" --no-restore -v minimal`
Expected: All tests PASS.

- [ ] **Step 2: Build entire solution**

Run: `dotnet build --no-restore -v minimal`
Expected: Build succeeded with 0 errors.

- [ ] **Step 3: Run all tests**

Run: `dotnet test Tests --no-restore -v minimal`
Expected: All tests PASS (integration tests may skip if no Camoufox).
