# WebInspect Improvements Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make WebInspect find all form inputs on the page regardless of whether they're inside `<form>` tags, and surface text inputs in interactive mode.

**Architecture:** Extend `InspectForms()` to scan for standalone inputs outside `<form>` elements and append them as a synthetic form entry. Extend `InspectInteractive()` to include all visible input fields by adding an `Inputs` property to the `InspectInteractive` record. Update tool descriptions to reflect the expanded coverage.

**Tech Stack:** .NET 10, AngleSharp (HTML parsing), Shouldly (assertions)

---

## File Structure

| File | Responsibility |
|------|---------------|
| `Domain/Contracts/IWebBrowser.cs` | **MODIFY** — Add `Inputs` field to `InspectInteractive` record |
| `Infrastructure/HtmlProcessing/HtmlInspector.cs` | **MODIFY** — Update `InspectForms()` to find standalone inputs, update `InspectInteractive()` to extract inputs, refactor `FindLabel` scope parameter |
| `Domain/Tools/Web/WebInspectTool.cs` | **MODIFY** — Update `Description` constant |
| `Tests/Unit/Infrastructure/HtmlInspectorTests.cs` | **MODIFY** — Add tests for standalone inputs and interactive inputs |

---

### Task 1: Forms Mode — Find Standalone Inputs

**Files:**
- Modify: `Infrastructure/HtmlProcessing/HtmlInspector.cs:520-543,569-601,604-631`
- Modify: `Tests/Unit/Infrastructure/HtmlInspectorTests.cs`

- [ ] **Step 1: Write the failing test for standalone inputs**

Add to `Tests/Unit/Infrastructure/HtmlInspectorTests.cs` inside the `#region Form Inspection Tests` section:

```csharp
[Fact]
public async Task InspectForms_FindsStandaloneInputsOutsideFormTags()
{
    const string html = """
                        <!DOCTYPE html>
                        <html><body>
                            <form id="login">
                                <input type="text" name="username" placeholder="Username">
                                <button type="submit">Login</button>
                            </form>
                            <div class="search-widget">
                                <input type="text" id="departureStation" placeholder="Departure">
                                <select id="arrivalStation" disabled>
                                    <option>Select arrival</option>
                                </select>
                                <button>Search</button>
                            </div>
                        </body></html>
                        """;

    var document = await ParseHtmlAsync(html);
    var forms = HtmlInspector.InspectForms(document, null);

    // Should find the actual form AND a synthetic entry for standalone inputs
    forms.Count.ShouldBe(2);

    // First entry: the real form
    forms[0].Name.ShouldBe("login");
    forms[0].Fields.Count.ShouldBe(1);

    // Second entry: standalone inputs (no form wrapper)
    var standalone = forms[1];
    standalone.Name.ShouldBeNull();
    standalone.Action.ShouldBeNull();
    standalone.Method.ShouldBeNull();
    standalone.Fields.Count.ShouldBe(2); // departureStation + arrivalStation
    standalone.Fields.ShouldContain(f => f.Placeholder == "Departure");
    standalone.Fields.ShouldContain(f => f.Type == "select");
}

[Fact]
public async Task InspectForms_NoStandaloneEntry_WhenAllInputsInsideForms()
{
    const string html = """
                        <!DOCTYPE html>
                        <html><body>
                            <form id="login">
                                <input type="text" name="username">
                                <button type="submit">Login</button>
                            </form>
                        </body></html>
                        """;

    var document = await ParseHtmlAsync(html);
    var forms = HtmlInspector.InspectForms(document, null);

    // Should only have the real form — no synthetic entry
    forms.Count.ShouldBe(1);
    forms[0].Name.ShouldBe("login");
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests --filter "FullyQualifiedName~HtmlInspectorTests.InspectForms_Finds" --no-restore -v minimal`
Expected: FAIL — `InspectForms_FindsStandaloneInputsOutsideFormTags` fails because standalone inputs are not returned.

- [ ] **Step 3: Update FindLabel to accept a broader scope**

In `Infrastructure/HtmlProcessing/HtmlInspector.cs`, the `FindLabel` method (line 604) searches for `label[for='id']` within the `form` parameter. For standalone inputs, we need to search within the document body instead. Rename the parameter from `form` to `scope` to clarify intent:

```csharp
private static string? FindLabel(IElement input, IElement scope)
{
    var id = input.GetAttribute("id");
    if (!string.IsNullOrEmpty(id))
    {
        var label = scope.QuerySelector($"label[for='{id}']");
        if (label != null)
        {
            return CollapseWhitespace(label.TextContent.Trim());
        }
    }

    var parentLabel = input.Closest("label");
    if (parentLabel == null)
    {
        return null;
    }

    var inputText = input.TextContent;
    var labelText = parentLabel.TextContent;

    var text = string.IsNullOrEmpty(inputText)
        ? labelText.Trim()
        : labelText.Replace(inputText, "").Trim();

    return CollapseWhitespace(text);
}
```

This is a pure rename — no behavior change. `ExtractFormFields` already passes its `form` parameter (now called `scope` at the call site) to `FindLabel`.

- [ ] **Step 4: Update InspectForms to find standalone inputs**

Replace the `InspectForms` method (lines 520-543) with:

```csharp
public static IReadOnlyList<InspectForm> InspectForms(IDocument document, string? selectorScope)
{
    var root = GetScopedElement(document, selectorScope);
    if (root == null)
    {
        return [];
    }

    var forms = root.QuerySelectorAll("form")
        .Select(form => new InspectForm(
            Name: form.GetAttribute("name"),
            Action: form.GetAttribute("action"),
            Method: form.GetAttribute("method")?.ToUpperInvariant(),
            Selector: GenerateSelector(form),
            Fields: ExtractFormFields(form),
            Buttons: ExtractFormButtons(form)))
        .ToList();

    // Find inputs outside any <form> tag
    var standaloneInputs = root.QuerySelectorAll("input, select, textarea")
        .Where(el => el.Closest("form") == null)
        .ToList();

    if (standaloneInputs.Count > 0)
    {
        var fields = standaloneInputs
            .Select(input => new
            {
                input,
                type = input.TagName.ToLowerInvariant() switch
                {
                    "select" => "select",
                    "textarea" => "textarea",
                    _ => input.GetAttribute("type") ?? "text"
                }
            })
            .Where(t => t.type is not ("hidden" or "submit" or "button"))
            .Select(t => new InspectFormField(
                Type: t.type,
                Name: t.input.GetAttribute("name"),
                Label: FindLabel(t.input, root),
                Placeholder: t.input.GetAttribute("placeholder"),
                Selector: GenerateSelector(t.input),
                Required: t.input.HasAttribute("required")))
            .ToList();

        if (fields.Count > 0)
        {
            forms.Add(new InspectForm(
                Name: null,
                Action: null,
                Method: null,
                Selector: "body",
                Fields: fields,
                Buttons: []));
        }
    }

    return forms;
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Tests --filter "FullyQualifiedName~HtmlInspectorTests.InspectForms" --no-restore -v minimal`
Expected: All InspectForms tests PASS (including existing ones).

- [ ] **Step 6: Commit**

```bash
git add Infrastructure/HtmlProcessing/HtmlInspector.cs Tests/Unit/Infrastructure/HtmlInspectorTests.cs
git commit -m "feat: make WebInspect forms mode find standalone inputs outside form tags"
```

---

### Task 2: Interactive Mode — Include Text Inputs

**Files:**
- Modify: `Domain/Contracts/IWebBrowser.cs:111-113`
- Modify: `Infrastructure/HtmlProcessing/HtmlInspector.cs:545-557`
- Modify: `Tests/Unit/Infrastructure/HtmlInspectorTests.cs`

- [ ] **Step 1: Write the failing test for inputs in interactive mode**

Add to `Tests/Unit/Infrastructure/HtmlInspectorTests.cs` inside the `#region Interactive Element Tests` section:

```csharp
[Fact]
public async Task InspectInteractive_FindsInputFields()
{
    const string html = """
                        <!DOCTYPE html>
                        <html><body>
                            <input type="text" id="search" placeholder="Search...">
                            <select name="category">
                                <option>All</option>
                                <option>Books</option>
                            </select>
                            <textarea name="notes" placeholder="Notes"></textarea>
                            <input type="hidden" name="token" value="abc">
                            <button>Submit</button>
                            <a href="/home">Home</a>
                        </body></html>
                        """;

    var document = await ParseHtmlAsync(html);
    var result = HtmlInspector.InspectInteractive(document, null);

    result.Buttons.Count.ShouldBe(1);
    result.Links.Count.ShouldBe(1);
    result.Inputs.Count.ShouldBe(3); // text + select + textarea (not hidden)
    result.Inputs.ShouldContain(f => f.Placeholder == "Search...");
    result.Inputs.ShouldContain(f => f.Type == "select");
    result.Inputs.ShouldContain(f => f.Type == "textarea");
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~HtmlInspectorTests.InspectInteractive_FindsInputFields" --no-restore -v minimal`
Expected: Compilation error — `InspectInteractive` does not contain a definition for `Inputs`.

- [ ] **Step 3: Add Inputs field to InspectInteractive record**

In `Domain/Contracts/IWebBrowser.cs`, replace the `InspectInteractive` record (lines 111-113):

```csharp
public record InspectInteractive(
    IReadOnlyList<InspectButton> Buttons,
    IReadOnlyList<InspectLink> Links,
    IReadOnlyList<InspectFormField> Inputs);
```

- [ ] **Step 4: Update InspectInteractive() to extract inputs**

In `Infrastructure/HtmlProcessing/HtmlInspector.cs`, replace the `InspectInteractive` method (lines 545-557):

```csharp
public static InspectInteractive InspectInteractive(IDocument document, string? selectorScope)
{
    var root = GetScopedElement(document, selectorScope);
    if (root == null)
    {
        return new InspectInteractive([], [], []);
    }

    var buttons = ExtractButtons(root);
    var links = ExtractLinks(root);
    var inputs = ExtractFormFields(root);

    return new InspectInteractive(buttons, links, inputs);
}
```

This reuses `ExtractFormFields(root)` which already queries `input, select, textarea`, filters out hidden/submit/button types, and extracts name/label/placeholder/selector/required. Passing `root` (the page body) means it finds ALL inputs on the page regardless of form membership.

- [ ] **Step 5: Fix existing InspectInteractive test constructor calls**

The existing tests create `InspectInteractive` with 2 arguments — they now need 3. However, these tests call `HtmlInspector.InspectInteractive()` which returns the result, so they should compile fine. If any test directly constructs `InspectInteractive([], [])`, update to `InspectInteractive([], [], [])`.

Check the test file for direct construction. The existing tests at `InspectInteractive_FindsButtonsAndLinks` and `InspectInteractive_GroupsSimilarElements` both call `HtmlInspector.InspectInteractive(document, null)` — no direct construction, so no change needed.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Tests --filter "FullyQualifiedName~HtmlInspectorTests.InspectInteractive" --no-restore -v minimal`
Expected: All InspectInteractive tests PASS.

- [ ] **Step 7: Fix any compilation errors from InspectInteractive constructor change**

Search for `new InspectInteractive(` across the codebase and update any call sites that pass only 2 arguments to pass 3 (adding `[]` for Inputs). The known call site is in `PlaywrightWebBrowser.cs` or wherever the fallback empty result is created.

Run: `dotnet build --no-restore -v minimal`
Expected: Build succeeded.

- [ ] **Step 8: Commit**

```bash
git add Domain/Contracts/IWebBrowser.cs Infrastructure/HtmlProcessing/HtmlInspector.cs Tests/Unit/Infrastructure/HtmlInspectorTests.cs
git commit -m "feat: add input fields to WebInspect interactive mode"
```

---

### Task 3: Update Tool Description

**Files:**
- Modify: `Domain/Tools/Web/WebInspectTool.cs:10-35`

- [ ] **Step 1: Update the Description constant**

In `Domain/Tools/Web/WebInspectTool.cs`, replace the `Description` constant (lines 10-35):

```csharp
protected const string Description =
    """
    Inspects the current page structure without returning full content.
    Use to explore large pages before extracting specific content with WebBrowse.

    Modes:
    - 'structure' (default): Smart page analysis with actionable suggestions
      - Detects main content area
      - Finds repeating elements (product cards, search results) with field detection
      - Identifies pagination/navigation
      - Returns hierarchical outline with selectors
      - Extracts JSON-LD structured data (Product, Article, Organization, etc.)
      - Provides suggestions like "Found 24 items: use selector='.product-card'"
    - 'search': Find visible TEXT in page, returns matches with context and selectors
    - 'forms': Detailed form inspection — finds ALL input fields including standalone inputs outside <form> tags
    - 'interactive': All interactive elements — buttons, links, and input fields with selectors
    - 'tables': Extract all tables as structured JSON with headers and rows

    IMPORTANT: To extract elements by CSS selector (e.g., '.product', '#main'), use WebBrowse
    with the selector parameter directly. The search mode only finds visible text content.

    Examples:
    - Analyze page structure: mode="structure" → get suggestions for extraction
    - Find text on page: mode="search", query="price"
    - Discover form fields: mode="forms" → finds inputs even if not inside <form> tags
    - Find all interactive elements: mode="interactive" → buttons, links, and input fields
    - Extract tables: mode="tables" → get structured data from all tables
    """;
```

- [ ] **Step 2: Verify build succeeds**

Run: `dotnet build Tests --no-restore -v minimal`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add Domain/Tools/Web/WebInspectTool.cs
git commit -m "feat: update WebInspect description to reflect standalone input and interactive input support"
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
