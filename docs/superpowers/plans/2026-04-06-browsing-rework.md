# Browsing Rework Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the CSS-selector/HTML-parsing interaction model with Playwright accessibility-tree snapshots for robust dynamic content handling, while keeping HTML-based content extraction for reading/scraping.

**Architecture:** Three tools (WebBrowse, WebSnapshot, WebAction) replace five (WebBrowse, WebInspect, WebClick, WidgetDetector, PostActionAnalyzer). Snapshots provide refs for interaction; WebBrowse handles content extraction. A single JavaScript function walks the DOM, computes accessibility properties, assigns `data-ref` attributes, and returns the formatted tree.

**Tech Stack:** .NET 10, Playwright (via Camoufox), AngleSharp (for markdown conversion), xUnit + Shouldly (tests)

---

### Task 1: New DTOs and Interface Extension

**Files:**
- Modify: `Domain/Contracts/IWebBrowser.cs:194-208` (append new types after existing enums)
- Modify: `Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs` (add stub methods)

- [ ] **Step 1: Add new types to IWebBrowser.cs**

Append after the existing `ClickAction` enum (line 206) and `ModalDismissed` record (line 208):

```csharp
// --- Snapshot types ---

public record SnapshotRequest(
    string SessionId,
    string? Selector = null);

public record SnapshotResult(
    string SessionId,
    string? Url,
    string? Snapshot,
    int RefCount,
    string? ErrorMessage);

// --- Action types ---

public enum WebActionType
{
    Click, Type, Fill, Select, Press, Clear,
    Hover, Drag, Back, Screenshot, HandleDialog
}

public record WebActionRequest(
    string SessionId,
    string? Ref = null,
    WebActionType Action = WebActionType.Click,
    string? Value = null,
    string? EndRef = null,
    bool WaitForNavigation = false,
    bool FullPage = false);

public enum WebActionStatus
{
    Success, Error, ElementNotFound, SessionNotFound, Timeout
}

public record WebActionResult(
    string SessionId,
    WebActionStatus Status,
    string? Url,
    bool NavigationOccurred,
    string? Snapshot,
    byte[]? ScreenshotData,
    string? DialogMessage,
    string? ErrorMessage);
```

- [ ] **Step 2: Add new methods to IWebBrowser interface**

Add to the `IWebBrowser` interface (after `InspectAsync`, around line 7):

```csharp
Task<SnapshotResult> SnapshotAsync(SnapshotRequest request, CancellationToken ct = default);
Task<WebActionResult> ActionAsync(WebActionRequest request, CancellationToken ct = default);
```

- [ ] **Step 3: Add stub implementations in PlaywrightWebBrowser**

Add stub methods to `Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs`:

```csharp
public Task<SnapshotResult> SnapshotAsync(SnapshotRequest request, CancellationToken ct = default)
    => throw new NotImplementedException("SnapshotAsync not yet implemented");

public Task<WebActionResult> ActionAsync(WebActionRequest request, CancellationToken ct = default)
    => throw new NotImplementedException("ActionAsync not yet implemented");
```

- [ ] **Step 4: Verify build**

Run: `dotnet build --no-restore -v minimal`
Expected: Build succeeded (0 errors). Stubs satisfy the interface.

- [ ] **Step 5: Commit**

```bash
git add Domain/Contracts/IWebBrowser.cs Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs
git commit -m "feat: add snapshot and action DTOs to IWebBrowser interface"
```

---

### Task 2: AccessibilitySnapshotService

The heart of the rework. A service that executes JavaScript on a Playwright page to walk the DOM, compute accessibility properties, assign `data-ref` attributes to interactive elements, and return a formatted tree.

**Files:**
- Create: `Infrastructure/Clients/Browser/AccessibilitySnapshotService.cs`

- [ ] **Step 1: Create AccessibilitySnapshotService**

Create `Infrastructure/Clients/Browser/AccessibilitySnapshotService.cs`:

```csharp
using Microsoft.Playwright;

namespace Infrastructure.Clients.Browser;

public class AccessibilitySnapshotService
{
    private const string SnapshotScript = """
        (selectorScope) => {
            let refCounter = 0;

            const implicitRoles = {
                'A': (el) => el.hasAttribute('href') ? 'link' : null,
                'BUTTON': () => 'button',
                'INPUT': (el) => {
                    const type = (el.getAttribute('type') || 'text').toLowerCase();
                    return ({ text:'textbox', search:'searchbox', email:'textbox', password:'textbox',
                        tel:'textbox', url:'textbox', number:'spinbutton', checkbox:'checkbox',
                        radio:'radio', range:'slider', submit:'button', reset:'button',
                        button:'button', image:'button' })[type] || 'textbox';
                },
                'SELECT': () => 'combobox',
                'TEXTAREA': () => 'textbox',
                'H1': () => 'heading', 'H2': () => 'heading', 'H3': () => 'heading',
                'H4': () => 'heading', 'H5': () => 'heading', 'H6': () => 'heading',
                'IMG': () => 'img',
                'NAV': () => 'navigation', 'MAIN': () => 'main',
                'HEADER': () => 'banner', 'FOOTER': () => 'contentinfo',
                'ASIDE': () => 'complementary', 'FORM': () => 'form',
                'TABLE': () => 'table', 'THEAD': () => 'rowgroup', 'TBODY': () => 'rowgroup',
                'TH': () => 'columnheader', 'TD': () => 'cell', 'TR': () => 'row',
                'UL': () => 'list', 'OL': () => 'list', 'LI': () => 'listitem',
                'OPTION': () => 'option', 'DIALOG': () => 'dialog',
                'DETAILS': () => 'group', 'SUMMARY': () => 'button',
                'PROGRESS': () => 'progressbar', 'METER': () => 'meter'
            };

            const interactiveRoles = new Set([
                'button','link','textbox','searchbox','combobox','checkbox','radio',
                'slider','spinbutton','switch','tab','menuitem','menuitemcheckbox',
                'menuitemradio','option','treeitem'
            ]);

            const structuralRoles = new Set([
                'heading','navigation','main','banner','contentinfo','complementary',
                'form','dialog','list','listitem','table','row','cell','columnheader',
                'img','group','tablist','tabpanel','menu','menubar','toolbar','tree',
                'grid','listbox','progressbar','meter','alert','status','region','rowgroup'
            ]);

            function getRole(el) {
                const explicit = el.getAttribute('role');
                if (explicit) return explicit;
                const fn = implicitRoles[el.tagName];
                return fn ? fn(el) : null;
            }

            function getName(el) {
                const ariaLabel = el.getAttribute('aria-label');
                if (ariaLabel) return ariaLabel.trim();

                const labelledBy = el.getAttribute('aria-labelledby');
                if (labelledBy) {
                    const text = labelledBy.split(/\s+/)
                        .map(id => document.getElementById(id)?.textContent?.trim())
                        .filter(Boolean).join(' ');
                    if (text) return text;
                }

                if (el.id) {
                    const label = document.querySelector(`label[for="${CSS.escape(el.id)}"]`);
                    if (label) return label.textContent.trim();
                }

                const parentLabel = el.closest('label');
                if (parentLabel && parentLabel !== el) {
                    const clone = parentLabel.cloneNode(true);
                    clone.querySelectorAll('input,select,textarea').forEach(c => c.remove());
                    const text = clone.textContent.trim();
                    if (text) return text;
                }

                const placeholder = el.getAttribute('placeholder');
                if (placeholder) return placeholder.trim();

                const title = el.getAttribute('title');
                if (title) return title.trim();

                const role = getRole(el);
                if (['button','link','heading','tab','menuitem','option','cell','columnheader'].includes(role)) {
                    const text = el.textContent?.trim();
                    if (text && text.length <= 80) return text;
                    if (text) return text.substring(0, 77) + '...';
                }

                if (el.tagName === 'IMG') return el.getAttribute('alt')?.trim() || null;
                return null;
            }

            function getState(el) {
                const s = [];
                if (el.getAttribute('aria-expanded') === 'true') s.push('expanded');
                if (el.getAttribute('aria-expanded') === 'false') s.push('collapsed');
                if (el.hasAttribute('disabled') || el.getAttribute('aria-disabled') === 'true') s.push('disabled');
                if (el.hasAttribute('required') || el.getAttribute('aria-required') === 'true') s.push('required');
                if (el.getAttribute('aria-checked') === 'true' || el.checked) s.push('checked');
                if (el.getAttribute('aria-selected') === 'true' || el.selected) s.push('selected');
                if (el.getAttribute('aria-pressed') === 'true') s.push('pressed');
                if (el.hasAttribute('readonly') || el.getAttribute('aria-readonly') === 'true') s.push('readonly');
                const hm = el.tagName.match(/^H(\d)$/);
                if (hm) s.push('level=' + hm[1]);
                return s;
            }

            function getValue(el) {
                if (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA') return el.value || null;
                if (el.tagName === 'SELECT') {
                    const opt = el.options[el.selectedIndex];
                    return opt ? opt.text : null;
                }
                return null;
            }

            function isHidden(el) {
                if (el.tagName === 'SCRIPT' || el.tagName === 'STYLE' || el.tagName === 'NOSCRIPT') return true;
                if (el.hidden || el.getAttribute('aria-hidden') === 'true') return true;
                const cs = getComputedStyle(el);
                return cs.display === 'none' || cs.visibility === 'hidden';
            }

            function isVisible(el) {
                const rect = el.getBoundingClientRect();
                return rect.width > 0 || rect.height > 0 ||
                       getComputedStyle(el).position === 'fixed';
            }

            function buildTree(el) {
                if (isHidden(el)) return null;
                const role = getRole(el);
                const children = Array.from(el.children)
                    .map(c => buildTree(c)).filter(Boolean);

                if (!role && children.length === 1) return children[0];
                if (!role && children.length === 0) return null;

                const isKnown = role && (interactiveRoles.has(role) || structuralRoles.has(role));
                if (isKnown) {
                    let ref = null;
                    if (interactiveRoles.has(role) && isVisible(el)) {
                        refCounter++;
                        ref = 'e' + refCounter;
                        el.setAttribute('data-ref', ref);
                    }
                    return { role, name: getName(el), state: getState(el),
                             value: getValue(el), ref, children };
                }

                return children.length > 0 ? { role: null, children } : null;
            }

            function format(node, indent) {
                if (!node) return '';
                if (!node.role) {
                    return node.children.map(c => format(c, indent)).filter(Boolean).join('\n');
                }
                let line = '  '.repeat(indent) + '- ' + node.role;
                if (node.name) line += ' "' + node.name.replace(/"/g, '\\"') + '"';
                if (node.ref) line += ' [ref=' + node.ref + ']';
                if (node.value) line += ': ' + node.value;
                for (const s of node.state) line += ' [' + s + ']';
                const cl = node.children.map(c => format(c, indent + 1)).filter(Boolean);
                return cl.length > 0 ? line + '\n' + cl.join('\n') : line;
            }

            const root = selectorScope ? document.querySelector(selectorScope) : document.body;
            if (!root) return { snapshot: '', refCount: 0 };
            const tree = buildTree(root);
            return { snapshot: tree ? format(tree, 0) : '', refCount: refCounter };
        }
        """;

    private const string FindContainerScript = """
        (selector) => {
            const el = document.querySelector(selector);
            if (!el) return null;
            const tags = ['FORM','SECTION','ARTICLE','MAIN','DIALOG','FIELDSET'];
            const roles = ['dialog','form','region','listbox','menu'];
            let container = el.parentElement;
            for (let i = 0; i < 4 && container; i++) {
                if (tags.includes(container.tagName) ||
                    roles.includes(container.getAttribute('role'))) {
                    if (container.id) return '#' + CSS.escape(container.id);
                    const idx = Array.from(container.parentElement?.children || [])
                        .filter(c => c.tagName === container.tagName).indexOf(container);
                    return container.tagName.toLowerCase() +
                        ':nth-of-type(' + (idx + 1) + ')';
                }
                container = container.parentElement;
                i++;
            }
            return null;
        }
        """;

    public async Task<SnapshotCaptureResult> CaptureAsync(
        IPage page, string? selectorScope, string sessionId)
    {
        var result = await page.EvaluateAsync<SnapshotJsResult>(SnapshotScript, selectorScope);
        return new SnapshotCaptureResult(result.Snapshot, result.RefCount);
    }

    public async Task<SnapshotCaptureResult> CaptureScopedAsync(
        IPage page, string targetSelector, string sessionId)
    {
        var containerSelector = await page.EvaluateAsync<string?>(
            FindContainerScript, targetSelector);

        return await CaptureAsync(page, containerSelector, sessionId);
    }

    public static ILocator ResolveRef(IPage page, string @ref)
        => page.Locator($"[data-ref='{@ref}']");

    public record SnapshotCaptureResult(string Snapshot, int RefCount);
    private record SnapshotJsResult(string Snapshot, int RefCount);
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build --no-restore -v minimal`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add Infrastructure/Clients/Browser/AccessibilitySnapshotService.cs
git commit -m "feat: add AccessibilitySnapshotService with DOM snapshot and ref management"
```

---

### Task 3: WebSnapshot Tool Stack

Full vertical slice: Domain tool, MCP wrapper, PlaywrightWebBrowser implementation, registration.

**Files:**
- Create: `Domain/Tools/Web/WebSnapshotTool.cs`
- Create: `McpServerWebSearch/McpTools/McpWebSnapshotTool.cs`
- Modify: `Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs` (replace SnapshotAsync stub)
- Modify: `McpServerWebSearch/Modules/ConfigModule.cs:62-65` (register new tool)

- [ ] **Step 1: Create WebSnapshotTool**

Create `Domain/Tools/Web/WebSnapshotTool.cs`:

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Web;

public class WebSnapshotTool(IWebBrowser browser)
{
    protected const string Name = "WebSnapshot";

    protected const string Description =
        """
        Returns the accessibility tree of the current page showing all elements:
        headings, text, buttons, links, form fields, dropdowns, and their current
        state (expanded, checked, disabled, etc.).

        Each interactive element has a ref you use with WebAction to interact with it.

        Use this to understand page state and find elements before interacting.
        Call after WebBrowse to see interactive elements, or after WebAction to see
        the full page when the scoped response isn't enough.
        """;

    protected async Task<JsonNode> RunAsync(
        string sessionId,
        string? selector,
        CancellationToken ct)
    {
        var request = new SnapshotRequest(sessionId, selector);
        var result = await browser.SnapshotAsync(request, ct);

        if (result.ErrorMessage is not null)
        {
            return new JsonObject
            {
                ["status"] = "error",
                ["sessionId"] = result.SessionId,
                ["message"] = result.ErrorMessage
            };
        }

        return new JsonObject
        {
            ["status"] = "success",
            ["sessionId"] = result.SessionId,
            ["url"] = result.Url,
            ["snapshot"] = result.Snapshot,
            ["refCount"] = result.RefCount
        };
    }
}
```

- [ ] **Step 2: Create McpWebSnapshotTool**

Create `McpServerWebSearch/McpTools/McpWebSnapshotTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Web;
using ModelContextProtocol.Server;

namespace McpServerWebSearch.McpTools;

[McpServerToolType]
public class McpWebSnapshotTool(IWebBrowser browser)
    : WebSnapshotTool(browser)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        [Description("CSS selector to limit snapshot scope (e.g. 'main', '.search-form'). Omit for full page.")]
        string? selector = null,
        CancellationToken ct = default)
    {
        var sessionId = context.Server.StateKey;
        var result = await RunAsync(sessionId, selector, ct);
        return ToolResponse.Create(result);
    }
}
```

- [ ] **Step 3: Implement SnapshotAsync in PlaywrightWebBrowser**

In `Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs`:

1. Add a field for the snapshot service. In the constructor area (around line 10), add a field:

```csharp
private readonly AccessibilitySnapshotService _snapshotService = new();
```

2. Replace the `SnapshotAsync` stub with:

```csharp
public async Task<SnapshotResult> SnapshotAsync(SnapshotRequest request, CancellationToken ct = default)
{
    var session = _sessions.Get(request.SessionId);
    if (session == null)
        return new SnapshotResult(request.SessionId, null, null, 0, "Session not found. Use WebBrowse first.");

    try
    {
        var result = await _snapshotService.CaptureAsync(session.Page, request.Selector, request.SessionId);
        return new SnapshotResult(request.SessionId, session.Page.Url, result.Snapshot, result.RefCount, null);
    }
    catch (Exception ex)
    {
        return new SnapshotResult(request.SessionId, session.Page.Url, null, 0, ex.Message);
    }
}
```

- [ ] **Step 4: Register in ConfigModule**

In `McpServerWebSearch/Modules/ConfigModule.cs`, find the tool registration block (around lines 62-65):

```csharp
.WithTools<McpWebSearchTool>()
.WithTools<McpWebBrowseTool>()
.WithTools<McpWebClickTool>()
.WithTools<McpWebInspectTool>()
```

Add after `McpWebInspectTool`:

```csharp
.WithTools<McpWebSnapshotTool>()
```

- [ ] **Step 5: Verify build**

Run: `dotnet build --no-restore -v minimal`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add Domain/Tools/Web/WebSnapshotTool.cs McpServerWebSearch/McpTools/McpWebSnapshotTool.cs Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs McpServerWebSearch/Modules/ConfigModule.cs
git commit -m "feat: add WebSnapshot tool with accessibility tree snapshots"
```

---

### Task 4: WebAction Tool Stack

Full vertical slice: Domain tool with action parsing, MCP wrapper, PlaywrightWebBrowser implementation with all 11 actions and smart wait, dialog support, registration.

**Files:**
- Create: `Domain/Tools/Web/WebActionTool.cs`
- Create: `McpServerWebSearch/McpTools/McpWebActionTool.cs`
- Create: `Tests/Unit/Infrastructure/WebActionToolTests.cs`
- Modify: `Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs` (replace ActionAsync stub)
- Modify: `Infrastructure/Clients/Browser/BrowserSessionManager.cs` (dialog support)
- Modify: `McpServerWebSearch/Modules/ConfigModule.cs` (register)

- [ ] **Step 1: Write failing test for action type parsing**

Create `Tests/Unit/Infrastructure/WebActionToolTests.cs`:

```csharp
using Domain.Contracts;
using Domain.Tools.Web;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class WebActionToolTests
{
    [Theory]
    [InlineData(null, WebActionType.Click)]
    [InlineData("", WebActionType.Click)]
    [InlineData("click", WebActionType.Click)]
    [InlineData("type", WebActionType.Type)]
    [InlineData("fill", WebActionType.Fill)]
    [InlineData("select", WebActionType.Select)]
    [InlineData("selectoption", WebActionType.Select)]
    [InlineData("press", WebActionType.Press)]
    [InlineData("clear", WebActionType.Clear)]
    [InlineData("hover", WebActionType.Hover)]
    [InlineData("drag", WebActionType.Drag)]
    [InlineData("back", WebActionType.Back)]
    [InlineData("screenshot", WebActionType.Screenshot)]
    [InlineData("handledialog", WebActionType.HandleDialog)]
    [InlineData("dialog", WebActionType.HandleDialog)]
    [InlineData("CLICK", WebActionType.Click)]
    [InlineData("Type", WebActionType.Type)]
    [InlineData("HOVER", WebActionType.Hover)]
    public void ParseActionType_ReturnsCorrectValue(string? input, WebActionType expected)
    {
        WebActionTool.ParseActionType(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("tap")]
    [InlineData("swipe")]
    public void ParseActionType_ThrowsForUnknownAction(string input)
    {
        Should.Throw<ArgumentException>(() => WebActionTool.ParseActionType(input));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~WebActionToolTests" --no-restore -v minimal`
Expected: Compilation error — `WebActionTool` does not exist.

- [ ] **Step 3: Create WebActionTool**

Create `Domain/Tools/Web/WebActionTool.cs`:

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Web;

public class WebActionTool(IWebBrowser browser)
{
    protected const string Name = "WebAction";

    protected const string Description =
        """
        Interacts with an element on the current page by ref from WebSnapshot.
        After the action, returns a snapshot of the affected area.

        Actions requiring ref:
        - 'click': Click the element
        - 'type': Type character-by-character (triggers autocomplete). Set value to text.
        - 'fill': Set input value directly (no keystroke events). Set value to text.
        - 'select': Select native dropdown option. Set value to option text.
        - 'press': Press keyboard key. Set value to key name (Enter, Tab, Escape, ArrowDown).
        - 'clear': Clear input field.
        - 'hover': Hover over element (triggers tooltips, menus).
        - 'drag': Drag element to target. Set endRef to destination element ref.

        Actions NOT requiring ref:
        - 'back': Navigate back in browser history.
        - 'screenshot': Take page screenshot. Optional ref scopes to element. fullPage=true for full page.
        - 'handleDialog': Accept/dismiss JS dialog. Set value to 'accept' or 'dismiss'.

        Workflow: WebSnapshot -> find ref -> WebAction(ref, action) -> read snapshot in response.
        For autocomplete: type partial text -> response shows options -> click option ref.
        """;

    protected async Task<JsonNode> RunAsync(
        string sessionId,
        string? @ref,
        string? action,
        string? value,
        string? endRef,
        bool waitForNavigation,
        bool fullPage,
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
            FullPage: fullPage);

        var result = await browser.ActionAsync(request, ct);

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
            response["snapshot"] = result.Snapshot;

        if (result.ScreenshotData is not null)
            response["screenshot"] = Convert.ToBase64String(result.ScreenshotData);

        if (result.DialogMessage is not null)
            response["dialogMessage"] = result.DialogMessage;

        return response;
    }

    public static WebActionType ParseActionType(string? action)
    {
        if (string.IsNullOrEmpty(action)) return WebActionType.Click;

        return action.ToLowerInvariant() switch
        {
            "click" => WebActionType.Click,
            "type" => WebActionType.Type,
            "fill" => WebActionType.Fill,
            "select" or "selectoption" => WebActionType.Select,
            "press" => WebActionType.Press,
            "clear" => WebActionType.Clear,
            "hover" => WebActionType.Hover,
            "drag" => WebActionType.Drag,
            "back" => WebActionType.Back,
            "screenshot" => WebActionType.Screenshot,
            "handledialog" or "dialog" => WebActionType.HandleDialog,
            _ => throw new ArgumentException($"Unknown action: {action}")
        };
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests --filter "FullyQualifiedName~WebActionToolTests" --no-restore -v minimal`
Expected: All tests PASS.

- [ ] **Step 5: Create McpWebActionTool**

Create `McpServerWebSearch/McpTools/McpWebActionTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Web;
using ModelContextProtocol.Server;

namespace McpServerWebSearch.McpTools;

[McpServerToolType]
public class McpWebActionTool(IWebBrowser browser)
    : WebActionTool(browser)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        [Description("Element ref from WebSnapshot (required for click, type, fill, select, press, clear, hover, drag)")]
        string? @ref = null,
        [Description("Action: 'click' (default), 'type', 'fill', 'select', 'press', 'clear', 'hover', 'drag', 'back', 'screenshot', 'handleDialog'")]
        string? action = null,
        [Description("Value: text to type/fill, option text for select, key name for press (Enter/Tab/Escape/ArrowDown), 'accept'/'dismiss' for handleDialog")]
        string? value = null,
        [Description("Target ref for drag action (drag from ref to endRef)")]
        string? endRef = null,
        [Description("Wait for page navigation after action (for clicks that load new pages)")]
        bool waitForNavigation = false,
        [Description("For screenshot: capture full scrollable page instead of viewport")]
        bool fullPage = false,
        CancellationToken ct = default)
    {
        var sessionId = context.Server.StateKey;
        var result = await RunAsync(sessionId, @ref, action, value, endRef, waitForNavigation, fullPage, ct);
        return ToolResponse.Create(result);
    }
}
```

- [ ] **Step 6: Add dialog support to BrowserSessionManager**

In `Infrastructure/Clients/Browser/BrowserSessionManager.cs`, update the `BrowserSession` record (around line 98) to add dialog state:

```csharp
public record BrowserSession(
    string SessionId,
    IPage Page,
    string CurrentUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastAccessedAt,
    IDialog? PendingDialog = null,
    string? LastDialogMessage = null);
```

Add a method to store/clear dialog state:

```csharp
public void SetPendingDialog(string sessionId, IDialog? dialog, string? message)
{
    if (_sessions.TryGetValue(sessionId, out var session))
    {
        _sessions[sessionId] = session with
        {
            PendingDialog = dialog,
            LastDialogMessage = message,
            LastAccessedAt = DateTimeOffset.UtcNow
        };
    }
}
```

- [ ] **Step 7: Implement ActionAsync in PlaywrightWebBrowser**

In `Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs`, replace the `ActionAsync` stub with the full implementation. Add these methods:

```csharp
public async Task<WebActionResult> ActionAsync(WebActionRequest request, CancellationToken ct = default)
{
    var session = _sessions.Get(request.SessionId);
    if (session == null)
        return new WebActionResult(request.SessionId, WebActionStatus.SessionNotFound,
            null, false, null, null, null, "Session not found. Use WebBrowse first.");

    var page = session.Page;
    var urlBefore = page.Url;

    try
    {
        return request.Action switch
        {
            WebActionType.Back => await ExecuteBackAsync(request, page, urlBefore, ct),
            WebActionType.Screenshot => await ExecuteScreenshotAsync(request, page),
            WebActionType.HandleDialog => await ExecuteHandleDialogAsync(request, page),
            _ => await ExecuteElementActionAsync(request, page, urlBefore, ct)
        };
    }
    catch (TimeoutException)
    {
        return new WebActionResult(request.SessionId, WebActionStatus.Timeout,
            page.Url, false, null, null, null, "Operation timed out.");
    }
    catch (PlaywrightException ex)
    {
        var status = ex.Message.Contains("not found") || ex.Message.Contains("no element")
            ? WebActionStatus.ElementNotFound
            : WebActionStatus.Error;
        return new WebActionResult(request.SessionId, status,
            page.Url, false, null, null, null, ex.Message);
    }
    catch (Exception ex)
    {
        return new WebActionResult(request.SessionId, WebActionStatus.Error,
            page.Url, false, null, null, null, ex.Message);
    }
}

private async Task<WebActionResult> ExecuteElementActionAsync(
    WebActionRequest request, IPage page, string urlBefore, CancellationToken ct)
{
    if (string.IsNullOrEmpty(request.Ref))
        return new WebActionResult(request.SessionId, WebActionStatus.Error,
            page.Url, false, null, null, null, $"ref is required for {request.Action} action.");

    var locator = AccessibilitySnapshotService.ResolveRef(page, request.Ref);
    await locator.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5000 });

    switch (request.Action)
    {
        case WebActionType.Click:
            await locator.ClickAsync();
            break;
        case WebActionType.Type:
            await locator.ClearAsync();
            await locator.PressSequentiallyAsync(request.Value ?? "", new() { Delay = 50 });
            break;
        case WebActionType.Fill:
            await locator.FillAsync(request.Value ?? "");
            break;
        case WebActionType.Select:
            await locator.SelectOptionAsync(request.Value ?? "");
            break;
        case WebActionType.Press:
            await locator.PressAsync(request.Value ?? "Enter");
            break;
        case WebActionType.Clear:
            await locator.ClearAsync();
            break;
        case WebActionType.Hover:
            await locator.HoverAsync();
            break;
        case WebActionType.Drag:
            if (string.IsNullOrEmpty(request.EndRef))
                return new WebActionResult(request.SessionId, WebActionStatus.Error,
                    page.Url, false, null, null, null, "endRef is required for drag action.");
            await locator.DragToAsync(AccessibilitySnapshotService.ResolveRef(page, request.EndRef));
            break;
    }

    if (request.WaitForNavigation)
    {
        try
        {
            await page.WaitForURLAsync(url => url != urlBefore,
                new() { Timeout = 10000 });
        }
        catch (TimeoutException)
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                new() { Timeout = 5000 });
        }
    }
    else
    {
        await SmartWaitAsync(page, request, ct);
    }

    var navigationOccurred = page.Url != urlBefore;
    _sessions.UpdateCurrentUrl(request.SessionId, page.Url);

    var targetSelector = $"[data-ref='{request.Ref}']";
    var snapshot = await _snapshotService.CaptureScopedAsync(page, targetSelector, request.SessionId);

    return new WebActionResult(request.SessionId, WebActionStatus.Success,
        page.Url, navigationOccurred, snapshot.Snapshot, null, null, null);
}

private async Task SmartWaitAsync(IPage page, WebActionRequest request, CancellationToken ct)
{
    var (maxWaitMs, intervalMs) = request.Action switch
    {
        WebActionType.Type => (2000, 200),
        WebActionType.Click => (2000, 200),
        WebActionType.Hover => (1000, 200),
        _ => (300, 300)
    };

    if (request.Ref is null || maxWaitMs <= intervalMs)
    {
        await Task.Delay(maxWaitMs, ct);
        return;
    }

    var targetSelector = $"[data-ref='{request.Ref}']";
    var previousHtml = await GetNearbyHtmlAsync(page, targetSelector);
    var elapsed = 0;
    while (elapsed < maxWaitMs)
    {
        await Task.Delay(intervalMs, ct);
        elapsed += intervalMs;
        var currentHtml = await GetNearbyHtmlAsync(page, targetSelector);
        if (currentHtml == previousHtml) break;
        previousHtml = currentHtml;
    }
}

private static async Task<string> GetNearbyHtmlAsync(IPage page, string targetSelector)
{
    return await page.EvaluateAsync<string>("""
        (selector) => {
            const el = document.querySelector(selector);
            if (!el) return '';
            const tags = ['FORM','SECTION','ARTICLE','MAIN','DIALOG','FIELDSET'];
            const roles = ['dialog','form','region','listbox','menu'];
            let container = el.parentElement;
            for (let i = 0; i < 4 && container; i++) {
                if (tags.includes(container.tagName) ||
                    roles.includes(container.getAttribute('role'))) break;
                container = container.parentElement;
            }
            return (container || el.parentElement || el).innerHTML.substring(0, 3000);
        }
    """, targetSelector);
}

private async Task<WebActionResult> ExecuteBackAsync(
    WebActionRequest request, IPage page, string urlBefore, CancellationToken ct)
{
    await page.GoBackAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
    _sessions.UpdateCurrentUrl(request.SessionId, page.Url);
    var snapshot = await _snapshotService.CaptureAsync(page, null, request.SessionId);

    return new WebActionResult(request.SessionId, WebActionStatus.Success,
        page.Url, page.Url != urlBefore, snapshot.Snapshot, null, null, null);
}

private async Task<WebActionResult> ExecuteScreenshotAsync(
    WebActionRequest request, IPage page)
{
    byte[] data;
    if (!string.IsNullOrEmpty(request.Ref))
    {
        var locator = AccessibilitySnapshotService.ResolveRef(page, request.Ref);
        data = await locator.ScreenshotAsync(new() { Type = ScreenshotType.Png });
    }
    else
    {
        data = await page.ScreenshotAsync(new()
        {
            Type = ScreenshotType.Png,
            FullPage = request.FullPage
        });
    }

    return new WebActionResult(request.SessionId, WebActionStatus.Success,
        page.Url, false, null, data, null, null);
}

private async Task<WebActionResult> ExecuteHandleDialogAsync(
    WebActionRequest request, IPage page)
{
    var session = _sessions.Get(request.SessionId)!;
    if (session.PendingDialog is null)
        return new WebActionResult(request.SessionId, WebActionStatus.Error,
            page.Url, false, null, null, null, "No dialog pending.");

    var dialog = session.PendingDialog;
    var message = dialog.Message;

    if (string.Equals(request.Value, "dismiss", StringComparison.OrdinalIgnoreCase))
        await dialog.DismissAsync();
    else
        await dialog.AcceptAsync(request.Value is null or "accept" ? "" : request.Value);

    _sessions.SetPendingDialog(request.SessionId, null, null);
    var snapshot = await _snapshotService.CaptureAsync(page, null, request.SessionId);

    return new WebActionResult(request.SessionId, WebActionStatus.Success,
        page.Url, false, snapshot.Snapshot, null, message, null);
}
```

- [ ] **Step 8: Register dialog handler on session creation**

In `Infrastructure/Clients/Browser/BrowserSessionManager.cs`, inside `GetOrCreateAsync`, after creating the page and session (around line 32-40), register the dialog handler:

```csharp
var page = await context.NewPageAsync();

page.Dialog += (_, dialog) =>
{
    if (_sessions.TryGetValue(sessionId, out var s))
    {
        _sessions[sessionId] = s with
        {
            PendingDialog = dialog,
            LastDialogMessage = dialog.Message
        };
    }
};

var session = new BrowserSession(
    SessionId: sessionId,
    Page: page,
    CurrentUrl: "about:blank",
    CreatedAt: DateTimeOffset.UtcNow,
    LastAccessedAt: DateTimeOffset.UtcNow);
```

- [ ] **Step 9: Register WebAction in ConfigModule**

In `McpServerWebSearch/Modules/ConfigModule.cs`, add after the WebSnapshot registration:

```csharp
.WithTools<McpWebActionTool>()
```

- [ ] **Step 10: Run tests and verify build**

Run: `dotnet test Tests --filter "FullyQualifiedName~WebActionToolTests" --no-restore -v minimal`
Expected: All tests PASS.

Run: `dotnet build --no-restore -v minimal`
Expected: Build succeeded.

- [ ] **Step 11: Commit**

```bash
git add Domain/Tools/Web/WebActionTool.cs McpServerWebSearch/McpTools/McpWebActionTool.cs Tests/Unit/Infrastructure/WebActionToolTests.cs Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs Infrastructure/Clients/Browser/BrowserSessionManager.cs McpServerWebSearch/Modules/ConfigModule.cs
git commit -m "feat: add WebAction tool with 11 action types and smart wait"
```

---

### Task 5: Simplify WebBrowse

Remove unused parameters, add structured data extraction, always use markdown format and DomContentLoaded wait strategy.

**Files:**
- Modify: `Domain/Contracts/IWebBrowser.cs:118-149` (simplify BrowseRequest, BrowseResult)
- Modify: `Domain/Tools/Web/WebBrowseTool.cs`
- Modify: `McpServerWebSearch/McpTools/McpWebBrowseTool.cs`
- Modify: `Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs:26-195` (simplify NavigateAsync)

- [ ] **Step 1: Simplify BrowseRequest**

In `Domain/Contracts/IWebBrowser.cs`, replace the `BrowseRequest` record (lines 118-136) with:

```csharp
public record BrowseRequest(
    string SessionId,
    string Url,
    string? Selector = null,
    int MaxLength = 10000,
    int Offset = 0,
    bool UseReadability = false,
    bool ScrollToLoad = false,
    int ScrollSteps = 3);
```

- [ ] **Step 2: Add StructuredData to BrowseResult, remove Links**

In `Domain/Contracts/IWebBrowser.cs`, replace the `BrowseResult` record (lines 138-149) with:

```csharp
public record BrowseResult(
    string SessionId,
    string Url,
    BrowseStatus Status,
    string? Title,
    string? Content,
    int ContentLength,
    bool Truncated,
    WebPageMetadata? Metadata,
    IReadOnlyList<StructuredData>? StructuredData,
    IReadOnlyList<ModalDismissed>? DismissedModals,
    string? ErrorMessage);
```

- [ ] **Step 3: Simplify WebBrowseTool**

Replace `Domain/Tools/Web/WebBrowseTool.cs` entirely:

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Web;

public class WebBrowseTool(IWebBrowser browser)
{
    protected const string Name = "WebBrowse";

    protected const string Description =
        """
        Navigates to a URL and returns page content as markdown.
        Maintains a persistent browser session (cookies, login state preserved).
        Automatically dismisses cookie popups, age gates, newsletter modals.

        Use selector to extract specific elements (e.g., selector=".product-card").
        Use maxLength/offset for pagination of long content.
        Use useReadability=true for clean article extraction (strips ads, nav, sidebars).
        Use scrollToLoad=true for pages with lazy-loaded content.

        Returns structured data (JSON-LD) when available on the page.

        For interacting with pages (clicking, filling forms), use WebSnapshot + WebAction.
        """;

    protected async Task<JsonNode> RunAsync(
        string sessionId,
        string url,
        string? selector,
        int maxLength,
        int offset,
        bool useReadability,
        bool scrollToLoad,
        int scrollSteps,
        CancellationToken ct)
    {
        maxLength = Math.Clamp(maxLength, 100, 100000);
        scrollSteps = Math.Clamp(scrollSteps, 1, 10);

        var request = new BrowseRequest(
            SessionId: sessionId,
            Url: url,
            Selector: selector,
            MaxLength: maxLength,
            Offset: offset,
            UseReadability: useReadability,
            ScrollToLoad: scrollToLoad,
            ScrollSteps: scrollSteps);

        var result = await browser.NavigateAsync(request, ct);

        if (result.Status is BrowseStatus.Error or BrowseStatus.SessionNotFound)
        {
            return new JsonObject
            {
                ["status"] = "error",
                ["sessionId"] = result.SessionId,
                ["url"] = result.Url,
                ["message"] = result.ErrorMessage
            };
        }

        var response = new JsonObject
        {
            ["status"] = result.Status == BrowseStatus.CaptchaRequired ? "captcha_required" : "success",
            ["sessionId"] = result.SessionId,
            ["url"] = result.Url,
            ["title"] = result.Title,
            ["content"] = result.Content,
            ["contentLength"] = result.ContentLength,
            ["truncated"] = result.Truncated
        };

        if (result.Metadata is not null)
        {
            response["metadata"] = new JsonObject
            {
                ["description"] = result.Metadata.Description,
                ["author"] = result.Metadata.Author,
                ["datePublished"] = result.Metadata.DatePublished,
                ["siteName"] = result.Metadata.SiteName
            };
        }

        if (result.StructuredData is { Count: > 0 })
        {
            var sdArray = new JsonArray();
            foreach (var sd in result.StructuredData)
            {
                sdArray.Add(new JsonObject
                {
                    ["type"] = sd.Type,
                    ["data"] = sd.RawJson
                });
            }
            response["structuredData"] = sdArray;
        }

        if (result.DismissedModals is { Count: > 0 })
        {
            var modals = new JsonArray();
            foreach (var m in result.DismissedModals)
                modals.Add(m.Type.ToString());
            response["dismissedModals"] = modals;
        }

        return response;
    }
}
```

- [ ] **Step 4: Simplify McpWebBrowseTool**

Replace `McpServerWebSearch/McpTools/McpWebBrowseTool.cs` entirely:

```csharp
using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Web;
using ModelContextProtocol.Server;

namespace McpServerWebSearch.McpTools;

[McpServerToolType]
public class McpWebBrowseTool(IWebBrowser browser)
    : WebBrowseTool(browser)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        [Description("The URL to navigate to")]
        string url,
        [Description("Maximum characters to return (100-100000, default: 10000)")]
        int maxLength = 10000,
        [Description("Character offset to start from (use 0 for beginning, increase to paginate)")]
        int offset = 0,
        [Description("CSS selector to extract specific elements (e.g. '.product-card', '#main'). Returns ALL matches.")]
        string? selector = null,
        [Description("Extract clean article content, stripping ads, navigation, sidebars")]
        bool useReadability = false,
        [Description("Scroll page to trigger lazy-loaded content")]
        bool scrollToLoad = false,
        [Description("Number of scroll steps for lazy loading (1-10, default: 3)")]
        int scrollSteps = 3,
        CancellationToken ct = default)
    {
        var sessionId = context.Server.StateKey;
        var result = await RunAsync(sessionId, url, selector, maxLength, offset,
            useReadability, scrollToLoad, scrollSteps, ct);
        return ToolResponse.Create(result);
    }
}
```

- [ ] **Step 5: Simplify NavigateAsync in PlaywrightWebBrowser**

In `Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs`, update `NavigateAsync` (lines 26-195) to:

1. Remove `MapWaitStrategy` call — always use `WaitUntilState.DOMContentLoaded`
2. Always dismiss modals (remove the `if (request.DismissModals)` check)
3. Always wait for DOM stability after navigation
4. Always use `WebFetchOutputFormat.Markdown` when calling `HtmlProcessor.ProcessAsync`
5. Remove `includeLinks` from `HtmlProcessor.ProcessAsync` call (pass `false`)
6. Add JSON-LD extraction after processing content
7. Build `BrowseResult` with `StructuredData` instead of `Links`

The key changes in NavigateAsync:

Replace the wait strategy line:
```csharp
// Before:
var waitUntil = MapWaitStrategy(request.WaitStrategy);
// After:
var waitUntil = WaitUntilState.DOMContentLoaded;
```

Replace the modal dismissal block:
```csharp
// Before:
if (request.DismissModals) { dismissedModals = await _modalDismisser.DismissModalsAsync(page, request.ModalConfig, ct); }
// After (always dismiss with default config):
var dismissedModals = await _modalDismisser.DismissModalsAsync(page, null, ct);
```

Always wait for stability:
```csharp
// Before:
if (request.WaitForStability) { await WaitForDomStabilityAsync(page, request.StabilityCheckMs); }
// After (always):
await WaitForDomStabilityAsync(page);
```

For JSON-LD extraction, add a helper method:

```csharp
private static IReadOnlyList<StructuredData> ExtractStructuredData(string html)
{
    var results = new List<StructuredData>();
    var matches = System.Text.RegularExpressions.Regex.Matches(html,
        @"<script[^>]*type=[""']application/ld\+json[""'][^>]*>(.*?)</script>",
        System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    foreach (System.Text.RegularExpressions.Match match in matches)
    {
        try
        {
            var json = match.Groups[1].Value.Trim();
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("@graph", out var graph) && graph.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in graph.EnumerateArray())
                {
                    var type = item.TryGetProperty("@type", out var t) ? t.GetString() : "Unknown";
                    results.Add(new StructuredData(type ?? "Unknown", item.GetRawText()));
                }
            }
            else
            {
                var type = root.TryGetProperty("@type", out var t) ? t.GetString() : "Unknown";
                results.Add(new StructuredData(type ?? "Unknown", json));
            }
        }
        catch { /* skip invalid JSON-LD */ }
    }

    return results;
}
```

Call it in `NavigateAsync` after getting the HTML, and pass to the result:

```csharp
var structuredData = ExtractStructuredData(html);
```

Update the `BrowseResult` construction to use `StructuredData: structuredData` instead of `Links: processed.Links`.

- [ ] **Step 6: Fix all compilation errors**

The `BrowseRequest` and `BrowseResult` signature changes will cause compilation errors in:
- `PlaywrightWebBrowser.NavigateAsync` — update constructor calls
- `PlaywrightWebBrowser.GetCurrentPageAsync` — update BrowseResult construction
- `PlaywrightWebBrowser.CreateErrorResult` — update BrowseResult construction
- Any existing tests that create BrowseRequest/BrowseResult

Run: `dotnet build --no-restore -v minimal 2>&1 | head -50`
Fix each error by updating the constructor calls to match the new signatures.

For `GetCurrentPageAsync` (lines 340-384), update the BrowseResult construction to use the new signature (no Links, add StructuredData: null).

For `CreateErrorResult` helper (lines 692-707), update to match new BrowseResult signature.

- [ ] **Step 7: Verify build and tests**

Run: `dotnet build --no-restore -v minimal`
Expected: Build succeeded.

Run: `dotnet test Tests --filter "FullyQualifiedName~Tests.Unit" --no-restore -v minimal`
Expected: All tests pass (some WebBrowse-related tests may need updating if they construct BrowseRequest/BrowseResult).

- [ ] **Step 8: Commit**

```bash
git add Domain/Contracts/IWebBrowser.cs Domain/Tools/Web/WebBrowseTool.cs McpServerWebSearch/McpTools/McpWebBrowseTool.cs Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs
git commit -m "feat: simplify WebBrowse — remove unused params, add structured data extraction"
```

---

### Task 6: Agent Prompt Rewrite

**Files:**
- Modify: `Domain/Prompts/WebBrowsingPrompt.cs`

- [ ] **Step 1: Replace WebBrowsingPrompt.cs content**

Replace the entire content of `Domain/Prompts/WebBrowsingPrompt.cs` with:

```csharp
namespace Domain.Prompts;

public static class WebBrowsingPrompt
{
    public const string AgentSystemPrompt =
        """
        ### Your Role

        You are Navigator, a web research assistant that helps users find, extract, and interact
        with information on the web. You have access to a persistent browser session that maintains
        state across multiple page interactions.

        ### Available Tools

        **WebSearch** - Search the web for information
        - Returns titles, URLs, snippets from search results
        - Use to find relevant pages before browsing

        **WebBrowse** - Navigate to URLs and extract content
        - Navigates to a URL and returns page content as markdown
        - Maintains a persistent browser session (cookies, login state preserved)
        - Automatically dismisses cookie popups, age gates, newsletter modals
        - Use selector parameter to extract specific elements (e.g., selector=".product-card")
        - Use maxLength/offset for pagination of long content
        - Use useReadability=true for clean article extraction (strips ads, nav, sidebars)
        - Use scrollToLoad=true for pages with lazy-loaded content
        - Returns JSON-LD structured data when available (check structuredData in response)

        **WebSnapshot** - See the current page state
        - Returns the accessibility tree showing ALL elements: headings, text, buttons, links,
          form fields, dropdowns, and their current state (expanded, checked, disabled, etc.)
        - Each interactive element has a ref you use with WebAction
        - This is your primary tool for understanding what's on the page and finding elements
        - Call this after WebBrowse to see interactive elements, or after WebAction to see results

        **WebAction** - Interact with elements
        - Target elements by ref from WebSnapshot
        - Actions: click, type (triggers autocomplete), fill (set value directly),
          select (native dropdowns), press (keyboard keys), clear, hover, drag
        - Special actions (no ref needed): back (navigate back), screenshot, handleDialog
        - Returns a snapshot of the area around the element after the action

        ### Core Workflows

        **Reading a page:**
        ```
        WebBrowse(url="...") → Read markdown content
        If truncated → Use offset to paginate OR selector to target specific section
        ```

        **Interacting with a page (forms, buttons, navigation):**
        ```
        WebBrowse(url="...") → Load the page
        WebSnapshot() → See all elements with refs
        WebAction(ref="...", action="fill", value="...") → Fill a field
        WebAction(ref="...", action="click") → Click a button
        ```

        **Autocomplete / Combobox fields:**
        ```
        WebSnapshot() → Find the input field ref
        WebAction(ref="input-ref", action="type", value="Odaw") → Type slowly to trigger suggestions
          → Response includes snapshot showing expanded dropdown with options
        WebAction(ref="option-ref", action="click") → Click the desired suggestion
        ```

        **Multi-page navigation:**
        ```
        WebSnapshot() → Find navigation link ref
        WebAction(ref="next-ref", action="click", waitForNavigation=true) → Navigate
        WebBrowse or WebSnapshot → See new page
        ```

        **Extracting structured data (products, search results):**
        ```
        WebBrowse(url="...") → Get page content
        If you need specific elements → WebBrowse(selector=".product-card")
        If you need structured data → Check structuredData in WebBrowse response
        ```

        **Hover menus / tooltips:**
        ```
        WebSnapshot() → Find element ref
        WebAction(ref="menu-ref", action="hover") → Hover triggers submenu/tooltip
          → Response snapshot shows newly visible elements
        WebAction(ref="submenu-item-ref", action="click") → Click revealed item
        ```

        **Drag and drop:**
        ```
        WebSnapshot() → Find source and target refs
        WebAction(ref="card-ref", action="drag", endRef="column-ref") → Drag to target
        ```

        **Going back:**
        ```
        WebAction(action="back") → Navigate to previous page
        WebSnapshot() → See previous page state
        ```

        **Visual debugging / CAPTCHAs:**
        ```
        WebAction(action="screenshot") → See what the page looks like visually
        WebAction(action="screenshot", ref="element-ref") → Screenshot specific element
        ```

        **JS dialogs (alert/confirm/prompt):**
        ```
        If an action triggers a JS dialog, the response will indicate a dialog is pending
        WebAction(action="handleDialog", value="accept") → Accept the dialog
        WebAction(action="handleDialog", value="dismiss") → Dismiss the dialog
        ```

        ### Key Principles

        1. **Snapshot before acting**: Always use WebSnapshot to see the current state before
           interacting. Don't guess selectors — use refs from the snapshot.

        2. **WebBrowse for content, WebSnapshot for state**: Use WebBrowse when you need to
           read text content (articles, product descriptions, search results). Use WebSnapshot
           when you need to understand page structure or find interactive elements.

        3. **Type for autocomplete, fill for direct input**: Use action="type" when the field
           has autocomplete/suggestions (types character-by-character to trigger JS handlers).
           Use action="fill" for simple text fields where you just need to set the value.

        4. **Read the snapshot after actions**: WebAction returns a snapshot of the affected
           area. If an autocomplete opened, you'll see the options. If a form submitted,
           you'll see the result. Call WebSnapshot for the full page if needed.

        5. **Start with Search**: Use WebSearch to find URLs rather than guessing.

        ### Error Recovery

        | Situation | Strategy |
        |-----------|----------|
        | Content truncated | Use offset to paginate or selector to target |
        | Can't find element | WebSnapshot to see what's available |
        | Autocomplete not opening | Try action="type" with partial text |
        | Page not loading | Try scrollToLoad=true for lazy content |
        | Session expired | Fresh WebBrowse to create new session |
        | Modal blocking content | Usually auto-dismissed; find close button via WebSnapshot |
        | JS dialog blocking | WebAction(action="handleDialog", value="accept" or "dismiss") |
        | Need to see page visually | WebAction(action="screenshot") for visual debugging |
        | Hidden hover content | WebAction(action="hover") to reveal tooltips/menus |
        | Need to go back | WebAction(action="back") instead of re-browsing previous URL |

        ### Response Style

        - Summarize findings rather than dumping raw content
        - Include source URLs when citing information
        - If content is partial, explain what's available and offer to get more
        - For data extraction, format results clearly (tables, lists)
        - When navigating, confirm each step's success before proceeding

        ### Limitations

        - Cannot access pages requiring CAPTCHA (unless CapSolver configured)
        - Cannot interact with file download dialogs
        - Session is per-conversation — resets between conversations
        - Some sites may block automated access
        """;
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build --no-restore -v minimal`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add Domain/Prompts/WebBrowsingPrompt.cs
git commit -m "feat: rewrite agent prompt for snapshot-based browsing model"
```

---

### Task 7: Remove Old Code

Delete old tools, infrastructure components, and their tests. Remove old methods from the interface.

**Files:**
- Delete: `Domain/Tools/Web/WebInspectTool.cs`
- Delete: `Domain/Tools/Web/WebClickTool.cs`
- Delete: `McpServerWebSearch/McpTools/McpWebInspectTool.cs`
- Delete: `McpServerWebSearch/McpTools/McpWebClickTool.cs`
- Delete: `Infrastructure/Clients/Browser/WidgetDetector.cs`
- Delete: `Infrastructure/Clients/Browser/PostActionAnalyzer.cs`
- Delete: `Infrastructure/HtmlProcessing/HtmlInspector.cs`
- Delete: `Tests/Unit/Infrastructure/HtmlInspectorTests.cs`
- Delete: `Tests/Unit/Infrastructure/WebClickToolTests.cs`
- Delete: `Tests/Unit/Infrastructure/PostActionAnalyzerTests.cs`
- Delete: `Tests/Unit/Infrastructure/WidgetDetectorTests.cs`
- Modify: `Domain/Contracts/IWebBrowser.cs` (remove old methods and DTOs)
- Modify: `Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs` (remove old methods)
- Modify: `McpServerWebSearch/Modules/ConfigModule.cs` (unregister old tools)

- [ ] **Step 1: Unregister old tools from ConfigModule**

In `McpServerWebSearch/Modules/ConfigModule.cs`, remove these lines:

```csharp
.WithTools<McpWebClickTool>()
.WithTools<McpWebInspectTool>()
```

- [ ] **Step 2: Delete old MCP wrappers and Domain tools**

```bash
rm McpServerWebSearch/McpTools/McpWebClickTool.cs
rm McpServerWebSearch/McpTools/McpWebInspectTool.cs
rm Domain/Tools/Web/WebClickTool.cs
rm Domain/Tools/Web/WebInspectTool.cs
```

- [ ] **Step 3: Remove old methods from IWebBrowser interface**

In `Domain/Contracts/IWebBrowser.cs`, remove these lines from the interface:

```csharp
Task<ClickResult> ClickAsync(ClickRequest request, CancellationToken ct = default);
Task<InspectResult> InspectAsync(InspectRequest request, CancellationToken ct = default);
```

Also remove all the old DTOs that are no longer used:
- `InspectRequest`, `InspectMode`, `InspectResult`, `InspectStructure`
- `ContentRegion`, `RepeatingElements`, `NavigationInfo`, `OutlineNode`
- `InspectSearchResult`, `InspectSearchMatch`
- `InspectForm`, `InspectFormField`, `InspectButton`, `InspectInteractive`, `InspectLink`
- `ExtractedTable`
- `ClickRequest`, `ClickResult`, `ClickStatus`, `ClickAction`

Keep: `BrowseRequest`, `BrowseResult`, `BrowseStatus`, `WebPageMetadata`, `StructuredData`, `ModalDismissed`, `SnapshotRequest`, `SnapshotResult`, `WebActionRequest`, `WebActionResult`, `WebActionType`, `WebActionStatus`.

- [ ] **Step 4: Remove old methods from PlaywrightWebBrowser**

In `Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs`, remove:
- `ClickAsync` method (lines ~197-338)
- `InspectAsync` method (lines ~386-481)
- `PerformClickAsync` method (lines ~572-614)
- Any helper methods only used by these deleted methods

- [ ] **Step 5: Delete old infrastructure components**

```bash
rm Infrastructure/Clients/Browser/WidgetDetector.cs
rm Infrastructure/Clients/Browser/PostActionAnalyzer.cs
rm Infrastructure/HtmlProcessing/HtmlInspector.cs
```

- [ ] **Step 6: Delete old test files**

```bash
rm Tests/Unit/Infrastructure/HtmlInspectorTests.cs
rm Tests/Unit/Infrastructure/WebClickToolTests.cs
rm Tests/Unit/Infrastructure/PostActionAnalyzerTests.cs
rm Tests/Unit/Infrastructure/WidgetDetectorTests.cs
```

- [ ] **Step 7: Fix compilation errors**

Run: `dotnet build --no-restore -v minimal 2>&1 | head -80`

Fix any remaining references to deleted types. Common issues:
- `PlaywrightWebBrowser` may reference `PostActionAnalyzer` or `WidgetDetector` — remove those usages
- `HtmlConverter` or `HtmlProcessor` may reference `ExtractedTable` or other deleted types — update if needed
- Any remaining `using` statements for deleted namespaces

Keep iterating until the build succeeds.

- [ ] **Step 8: Verify build and tests**

Run: `dotnet build --no-restore -v minimal`
Expected: Build succeeded.

Run: `dotnet test Tests --filter "FullyQualifiedName~Tests.Unit" --no-restore -v minimal`
Expected: All remaining tests pass. Test count will be lower (deleted tests removed).

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "refactor: remove WebInspect, WebClick, WidgetDetector, PostActionAnalyzer, HtmlInspector"
```

---

### Task 8: Full Verification

**Files:** None (verification only)

- [ ] **Step 1: Full build**

Run: `dotnet build --no-restore -v minimal`
Expected: Build succeeded, 0 errors, 0 warnings.

- [ ] **Step 2: Run all unit tests**

Run: `dotnet test Tests --filter "FullyQualifiedName~Tests.Unit" --no-restore -v minimal`
Expected: All tests pass.

- [ ] **Step 3: Verify tool registration**

Check that ConfigModule registers exactly these tools:
```
McpWebSearchTool
McpWebBrowseTool
McpWebSnapshotTool
McpWebActionTool
```

Run: `grep -n "WithTools" McpServerWebSearch/Modules/ConfigModule.cs`
Expected: Four lines, one per tool above.
