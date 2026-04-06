# WebInspect Improvements — Design Spec

**Date:** 2026-04-06
**Problem:** WebInspect's `forms` mode only finds inputs inside `<form>` tags, and `interactive` mode only finds buttons and links. This means the agent cannot discover text inputs, selects, or textareas that exist outside `<form>` elements (common in modern SPAs and custom widget libraries). On sites like japantravel.navitime.com, the departure station textbox is invisible to both modes.

**Solution:** Improve both modes to surface all form inputs on the page regardless of whether they're inside a `<form>` tag.

---

## Change 1: Forms Mode — Find Standalone Inputs

**File:** `Infrastructure/HtmlProcessing/HtmlInspector.cs` — `InspectForms()` method (line 520)

Currently, `InspectForms()` queries only `root.QuerySelectorAll("form")` and extracts fields from each form. Inputs outside any `<form>` tag are missed entirely.

**Fix:** After collecting form-based fields, scan for all `input, select, textarea` elements in the page root that are NOT inside any `<form>` tag. If any standalone inputs are found, append them as a synthetic `InspectForm` entry with:
- `Name: null`
- `Action: null`
- `Method: null`
- `Selector: null`
- `Fields`: the standalone inputs (reusing existing `ExtractFormFields` logic)
- `Buttons`: empty list

This requires no DTO changes — `InspectForm` already supports null Name/Action/Method. The `ExtractFormFields` helper needs to be generalized to accept `IElement` (it already does — its parameter is `IElement form`, which works for any element including the page root).

To identify standalone inputs: query all `input, select, textarea` from root, then filter to those where `element.Closest("form") == null`.

## Change 2: Interactive Mode — Include Text Inputs

**File:** `Infrastructure/HtmlProcessing/HtmlInspector.cs` — `InspectInteractive()` method (line 545)
**File:** `Domain/Contracts/IWebBrowser.cs` — `InspectInteractive` record (line 111)

Currently, `InspectInteractive` only has `Buttons` and `Links`. Text inputs are not surfaced.

**Fix:** Add an `Inputs` field to the `InspectInteractive` record:

```csharp
public record InspectInteractive(
    IReadOnlyList<InspectButton> Buttons,
    IReadOnlyList<InspectLink> Links,
    IReadOnlyList<InspectFormField> Inputs);
```

In `InspectInteractive()`, extract all visible input fields from the root element (all `input, select, textarea` excluding hidden/submit/button types) using the same logic as `ExtractFormFields`. This gives the agent a complete picture of all interactive elements on the page — buttons, links, AND form inputs.

## Change 3: Tool Description Update

**File:** `Domain/Tools/Web/WebInspectTool.cs`

Update the description for both modes:
- `forms`: "Detailed form inspection with all fields and buttons, **including inputs outside form tags**"
- `interactive`: "All interactive elements: buttons, links, **and input fields** with selectors"

## Testing

- Unit test for `InspectForms`: HTML fixture with inputs both inside and outside `<form>` tags — verify standalone inputs appear in the result
- Unit test for `InspectInteractive`: HTML fixture with buttons, links, AND text inputs — verify inputs appear in the `Inputs` field
- Verify existing tests still pass (the forms inside `<form>` tags should still work identically)
