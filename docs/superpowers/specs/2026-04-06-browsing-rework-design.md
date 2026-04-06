# Browsing Rework: Snapshot-Based Interaction + Simplified Extraction

## Problem

The agent struggles with dynamic web interactions — especially custom autocomplete widgets, JS-driven dropdowns, and modern form patterns. The root cause is architectural: our tools parse static HTML (AngleSharp) for page understanding and use CSS-selector-based interaction with complex heuristic widget detection (WidgetDetector, PostActionAnalyzer). This approach fundamentally can't see dynamic DOM state.

Real example: on japantravel.navitime.com, the agent typed "Odawara" into an autocomplete input, saw the ARIA announcement "1 result is available", but couldn't find or interact with the suggestion elements because WidgetDetector's hardcoded CSS patterns didn't match the site's implementation.

## Solution

Replace the interaction model with Playwright's accessibility tree snapshots. The accessibility tree naturally represents dynamic state — open comboboxes, expanded autocomplete suggestions, form field values — as first-class elements with stable refs for targeting. Keep HTML-based content extraction for reading/scraping where it adds value over snapshots.

## Architecture

### Three tools replace five:

| New Tool | Replaces | Purpose |
|----------|----------|---------|
| **WebBrowse** | WebBrowse (simplified) | Navigate + read content as markdown |
| **WebSnapshot** | WebInspect (all modes) | Accessibility tree with refs |
| **WebAction** | WebClick | Interact by ref from snapshot |

WebSearch remains unchanged.

### Killed components:
- WebInspect tool (all 5 modes)
- WebClick tool
- WidgetDetector
- PostActionAnalyzer
- HtmlInspector (except `HtmlConverter` for markdown conversion)

---

## Tool Specifications

### WebBrowse (simplified)

Navigate to a URL and return page content as markdown.

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `url` | string | yes | URL to navigate to |
| `selector` | string | no | CSS selector to extract specific elements |
| `maxLength` | int | no | Max characters to return (100-100000, default 10000) |
| `offset` | int | no | Character position to start from (default 0) |
| `useReadability` | bool | no | Extract article content, strip page chrome (default false) |
| `scrollToLoad` | bool | no | Scroll through page to trigger lazy-loaded content (default false) |
| `scrollSteps` | int | no | Number of scroll intervals (1-10, default 3) |

**Removed parameters:** `format` (always markdown), `waitStrategy`, `waitSelector`, `waitForStability`, `extraDelayMs`, `dismissModals` (always on), `includeLinks`.

**Wait strategy:** Always use `DomContentLoaded` + wait for DOM stability (the most reliable default). No user-facing toggle.

**Response:**

```json
{
  "status": "success",
  "url": "https://...",
  "content": "# Page Title\n\nMarkdown content...",
  "contentLength": 45000,
  "truncated": true,
  "metadata": {
    "title": "Page Title",
    "description": "Meta description",
    "author": "Author Name",
    "datePublished": "2026-01-15",
    "siteName": "Example.com"
  },
  "structuredData": [
    { "type": "Product", "data": { ... } }
  ],
  "dismissedModals": ["CookieConsent"]
}
```

**Changes from current:**
- Dropped `format` parameter — always markdown
- Dropped all wait strategy parameters — uses smart default
- Dropped `includeLinks` — links are inline in markdown
- Added `structuredData` to response — JSON-LD extraction moves here from WebInspect since it's invisible to snapshots
- Modal dismissal is always-on, response just lists what was dismissed

### WebSnapshot

Return the accessibility tree of the current page. This is the primary tool for understanding page state, finding interactive elements, and getting refs for WebAction.

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `selector` | string | no | CSS selector to scope the snapshot to a subtree |

**Response:** Accessibility tree in a compact text format, similar to Playwright MCP's snapshot. Each element has:
- Role (button, textbox, combobox, link, heading, etc.)
- Name/label
- State (expanded, checked, disabled, required, etc.)
- Value (for inputs)
- Ref (stable identifier for targeting with WebAction)
- Children (nested tree)

Example output after typing "Odawara" into an autocomplete:

```
[page] Direct Routes Schedule Finder
  [heading] Schedule Finder
  [combobox "Departure Station" expanded] Odawara
    [listbox]
      [option ref="opt1"] Odawara (小田原)
  [combobox "Arrival Station" disabled]
    [option] Select the arrival station
  [button ref="search"] Search
```

The agent immediately sees: the combobox is expanded, there's one option "Odawara (小田原)" with ref "opt1", and can click it with WebAction.

**No modes** — the accessibility tree contains everything: form fields, buttons, links, inputs, headings, landmarks. The agent reads what it needs.

### WebAction

Interact with an element by ref from WebSnapshot. After the action, automatically returns a new snapshot of the affected area.

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `ref` | string | no | Element ref from WebSnapshot (required for most actions, not for back/screenshot/handleDialog) |
| `action` | string | no | Action type (default "click") |
| `value` | string | no | Value for type/fill/select/press/handleDialog actions |
| `endRef` | string | no | Target ref for drag action |
| `waitForNavigation` | bool | no | Wait for URL change after action (default false) |
| `fullPage` | bool | no | For screenshot: capture full scrollable page vs viewport (default false) |

**Actions:**

| Action | Description | `value` | `ref` required? |
|--------|-------------|---------|-----------------|
| `click` | Click the element | — | yes |
| `type` | Type character-by-character (triggers autocomplete) | Text to type | yes |
| `fill` | Set value directly (no keystroke events) | Text to set | yes |
| `select` | Select option from native `<select>` | Option text | yes |
| `press` | Press keyboard key | Key name (Enter, Tab, Escape, ArrowDown) | yes |
| `clear` | Clear input field | — | yes |
| `hover` | Hover over element (triggers tooltips, hover menus) | — | yes |
| `drag` | Drag from ref to endRef | — | yes (+ `endRef`) |
| `back` | Navigate back in browser history | — | no |
| `screenshot` | Take screenshot, returns image | — | no (optional: scope to ref) |
| `handleDialog` | Accept or dismiss JS alert/confirm/prompt | "accept" or "dismiss" (for prompts: the text to enter) | no |

**Smart wait after action:**
- For `type` and `click` on input/combobox: poll DOM near target every 200ms for up to 2 seconds, stop early when stable. This handles both fast and debounced autocompletes.
- For `hover`: poll for 1 second (tooltips/menus appear quickly).
- For `back`: wait for navigation to complete.
- For `screenshot`: no wait needed, returns image immediately.
- For `handleDialog`: no wait needed, acts on the pending dialog.
- For all other actions: fixed 300ms wait.
- For `waitForNavigation=true`: wait for URL change or network idle.

**Dialog handling:**
- JS dialogs (alert, confirm, prompt) block all page interaction until handled.
- When a dialog is pending, WebSnapshot and other actions will fail. The agent must use `handleDialog` first.
- The response includes the dialog message text so the agent can decide whether to accept or dismiss.

**Response:**

```json
{
  "status": "success",
  "url": "https://...",
  "navigationOccurred": false,
  "snapshot": "[combobox 'Departure Station' expanded] Odawara\n  [listbox]\n    [option ref='opt1'] Odawara (小田原)"
}
```

The snapshot in the response is scoped to the area around the target element — not the full page. The agent can call WebSnapshot for a full-page view if needed.

---

## Agent Prompt

Replace `Domain/Prompts/WebBrowsingPrompt.cs` entirely:

```
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
If you need structured data → Check structuredData in WebBrowse response (JSON-LD)
```

**Hover menus / tooltips:**
```
WebSnapshot() → Find element ref
WebAction(ref="menu-ref", action="hover") → Hover triggers submenu/tooltip
  → Response snapshot shows newly visible elements
WebAction(ref="submenu-item-ref", action="click") → Click revealed item
```

**Drag and drop (maps, sliders, kanban):**
```
WebSnapshot() → Find source and target refs
WebAction(ref="card-ref", action="drag", endRef="column-ref") → Drag card to column
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
   has autocomplete/suggestions. Use action="fill" for simple text fields where you
   just need to set the value.

4. **Read the snapshot after actions**: WebAction returns a snapshot of the affected
   area. If an autocomplete opened, you'll see the options. If a form submitted,
   you'll see the result.

5. **Start with Search**: Use WebSearch to find URLs rather than guessing.

### Error Recovery

| Situation | Strategy |
|-----------|----------|
| Content truncated | Use offset to paginate or selector to target |
| Can't find element | WebSnapshot to see what's available |
| Autocomplete not opening | Try action="type" with partial text |
| Page not loading | Try scrollToLoad=true for lazy content |
| Session expired | Fresh WebBrowse to create new session |
| Modal blocking content | Usually auto-dismissed; if not, find close button in snapshot |
| JS dialog blocking | Use WebAction(action="handleDialog", value="accept" or "dismiss") |
| Need to see page visually | WebAction(action="screenshot") for visual debugging |
| Hidden hover content | WebAction(action="hover") to reveal tooltips/menus |
| Need to go back | WebAction(action="back") instead of re-browsing previous URL |

### Response Style

- Summarize findings rather than dumping raw content
- Include source URLs when citing information
- If content is partial, explain what's available and offer to get more
- For data extraction, format results clearly (tables, lists)
- When navigating, confirm each step's success before proceeding
```

---

## Implementation Notes

### Browser Backend

All three tools share the same Playwright browser session (via Camoufox). The accessibility tree comes from Playwright's `page.accessibility.snapshot()` API. Refs are generated from the snapshot and mapped to Playwright locators for WebAction.

### Session Management

Sessions are transparent to the agent — it never sees or manages session IDs. The MCP wrapper layer gets `sessionId` from `context.Server.StateKey`, which is unique per MCP server connection. This means:

- Each MCP connection (e.g., each subagent) gets its own session automatically
- Each session maps to its own Playwright `IPage` via `BrowserSessionManager`
- Parallel subagents researching different topics get independent browser pages with no conflicts
- The existing `BrowserSessionManager` handles session lifecycle (create on first use, cleanup on dispose)

All three new tools (WebBrowse, WebSnapshot, WebAction) must follow this pattern:
```csharp
// In McpWebBrowseTool, McpWebSnapshotTool, McpWebActionTool:
var sessionId = context.Server.StateKey;
```

The Domain-layer tool classes accept `sessionId` as their first parameter (same as current tools). The ref→locator mapping in `AccessibilitySnapshotService` must also be scoped per session.

### Ref System

Refs must be stable within a single snapshot but don't need to persist across snapshots. Implementation options:
- Use Playwright's built-in accessibility snapshot refs if available
- Generate short sequential refs (e.g., "e1", "e2", "e3") mapped to element handles
- Store ref→locator mapping in the browser session, cleared on each new snapshot

Ref maps are per-session: `Dictionary<string, Dictionary<string, ILocator>>` keyed by sessionId then ref. When a new snapshot is taken for a session, its ref map is replaced.

### Scoped Snapshots in WebAction

After an action, the response snapshot should be scoped to the relevant area — not the full page. Strategy: find the semantic container (form, section, dialog) around the target element, snapshot that subtree. Similar to PostActionAnalyzer's focused area extraction but using the accessibility tree instead of HTML.

### Migration Path

Since WebBrowse keeps its name and core purpose (navigate + read), existing agent behavior for content extraction doesn't break. The interaction model changes completely (CSS selectors → refs), so the prompt update is critical. WebSearch is untouched.

### Files to Create/Modify

**New files:**
- `Domain/Tools/Web/WebSnapshotTool.cs` — WebSnapshot tool
- `Domain/Tools/Web/WebActionTool.cs` — WebAction tool
- `McpServerWebSearch/McpTools/McpWebSnapshotTool.cs` — MCP wrapper
- `McpServerWebSearch/McpTools/McpWebActionTool.cs` — MCP wrapper
- `Infrastructure/Clients/Browser/AccessibilitySnapshotService.cs` — Snapshot capture + ref management

**Modified files:**
- `Domain/Tools/Web/WebBrowseTool.cs` — Simplify parameters, add structuredData to response
- `Domain/Contracts/IWebBrowser.cs` — Update interface (remove click-related methods, add snapshot/action)
- `Domain/Prompts/WebBrowsingPrompt.cs` — Complete rewrite
- `Infrastructure/Clients/Browser/PlaywrightWebBrowser.cs` — Add snapshot/action methods, simplify browse
- `McpServerWebSearch/McpTools/McpWebBrowseTool.cs` — Update parameter descriptions
- `McpServerWebSearch/Modules/ConfigModule.cs` — Register new MCP tools, remove old ones

**Deleted files:**
- `Domain/Tools/Web/WebInspectTool.cs`
- `Domain/Tools/Web/WebClickTool.cs`
- `McpServerWebSearch/McpTools/McpWebInspectTool.cs`
- `McpServerWebSearch/McpTools/McpWebClickTool.cs`
- `Infrastructure/Clients/Browser/WidgetDetector.cs`
- `Infrastructure/Clients/Browser/PostActionAnalyzer.cs`
- `Infrastructure/HtmlProcessing/HtmlInspector.cs` (keep `HtmlConverter.cs`)

**Test files:**
- New tests for WebSnapshot, WebAction, AccessibilitySnapshotService
- Update WebBrowse tests for simplified parameters
- Delete HtmlInspector tests, WebClick tests, WidgetDetector tests, PostActionAnalyzer tests
