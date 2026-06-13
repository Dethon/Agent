# WebChat Refresh — Plan C: The Hearth Navigation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the topic sidebar / mobile horizontal-strip with "The Hearth": a draggable bottom sheet (peek → half → full detents) + a thin bottom bar on mobile, which un-docks via a `@media (min-width: 768px)` breakpoint into a pinned ~320px left rail on desktop — one component, two postures.

**Architecture:** `TopicList.razor` is rewritten to render the Hearth (peek bar + bottom bar + draggable sheet body), reusing every existing binding (`SelectTopic`, `SelectAgent`, `CreateNewTopic`, `RemoveTopic`, streaming/unread). The agent dropdown becomes a reusable `AgentSwitcher` (segmented strip on desktop, native `<dialog>` popover on mobile) consuming Plan B's activity selectors. The live drag runs entirely in JS (`app.js` writes a `--sheet-offset` CSS var via `requestAnimationFrame`; one `DotNetObjectReference` callback on release commits the settled detent), mirroring the existing `chatScroll`/`chatInput` interop idiom. ⌘K opens search. Native `<dialog>` and `prefers-reduced-motion` (added in Plan A) provide accessibility.

**Tech Stack:** Blazor WebAssembly, CSS (container-free `@media`, `dvh`, transforms, scroll-snap on the inner list only), `app.js` pointer-gesture interop, Playwright E2E (no bUnit).

**Scope note:** Plan C of three. **Depends on Plan A** (Ember tokens, fonts, reduced-motion) and **Plan B** (`UnreadSelectors`, `AgentActivityStore`, `AgentActivitySelectors`). Verification is build + Playwright E2E + manual visual QA; the exact detent heights, paddings, and the gesture capture threshold are the implementation-time tunables called out in spec §10.

---

## File Structure

| File | Change | Responsibility |
|------|--------|----------------|
| `WebChat.Client/Components/AgentSelector.razor` | Modify | Becomes `AgentSwitcher`: `Segmented`/`Popover` modes + activity dots |
| `WebChat.Client/Components/TopicList.razor` | Rewrite | The Hearth: peek bar + bottom bar + draggable sheet / desktop rail |
| `WebChat.Client/Components/Chat/ChatContainer.razor` | Modify | Sheet overlays chat on mobile; rail sibling on desktop |
| `WebChat.Client/wwwroot/css/app.css` | Modify | Delete `.topic-sidebar/.topic-list/.topic-item` + the 768px strip reflow; add Hearth CSS (sheet detents, desktop rail, dvh) |
| `WebChat.Client/wwwroot/app.js` | Modify | `hearthSheet` gesture module + global ⌘K listener |
| `Tests/E2E/WebChat/HearthNavigationE2ETests.cs` | Create | Rail-at-desktop, sheet detents, agent switch, ⌘K |

The component reuses these existing `TopicList` members verbatim (confirmed present): `HandleTopicClick → SelectTopic`, `HandleNewTopic → CreateNewTopic`, `SelectAgent`, `ShowDeleteConfirm`/`ConfirmDelete → RemoveTopic`/`CancelDelete`, `IsTopicStreaming`, `GetUnreadCount` (now via `UnreadSelectors`), `filteredTopics` recency order, the `#tooltip` div.

---

### Task 1: AgentSwitcher (segmented strip + popover) with activity dots

**Files:**
- Modify: `WebChat.Client/Components/AgentSelector.razor` (the currently-unused reusable dropdown)

Give the reusable component two modes and an activity set. Desktop uses `Segmented`; mobile uses `Popover` (native `<dialog>`).

- [ ] **Step 1: Rewrite `AgentSelector.razor` to support modes + dots**

Replace its contents with:

```razor
@using Domain.DTOs.Channel
@using WebChat.Client.Helpers

@if (Mode == SwitcherMode.Segmented)
{
    <div class="agent-segmented" role="radiogroup" aria-label="Select agent">
        @foreach (var agent in Agents)
        {
            <button class="agent-seg @(agent.Id == SelectedAgentId ? "on" : "")"
                    role="radio" aria-checked="@(agent.Id == SelectedAgentId)"
                    @onclick="() => Select(agent.Id)">
                <span class="agent-mono" style="background:@AvatarHelper.GetColorForUser(agent.Id)">@AvatarHelper.GetInitials(agent.Name)</span>
                <span class="agent-seg-name">@agent.Name</span>
                @if (ActiveAgentIds.Contains(agent.Id) && agent.Id != SelectedAgentId)
                {
                    <span class="agent-activity-dot" title="Active"></span>
                }
            </button>
        }
    </div>
}
else
{
    <button class="agent-chip" @onclick="OpenPopover" @onclick:stopPropagation="true" aria-haspopup="dialog">
        <span class="agent-mono" style="background:@AvatarHelper.GetColorForUser(SelectedAgentId)">@AvatarHelper.GetInitials(SelectedAgentName)</span>
        <span class="agent-chip-name">@SelectedAgentName</span>
        @if (ActiveAgentIds.Any(id => id != SelectedAgentId))
        {
            <span class="agent-activity-dot"></span>
        }
    </button>

    <dialog class="agent-popover" @ref="_popover">
        <div class="agent-popover-title">Switch agent</div>
        @foreach (var agent in Agents)
        {
            <button class="agent-popover-item @(agent.Id == SelectedAgentId ? "on" : "")"
                    @onclick="() => Select(agent.Id)">
                <span class="agent-mono" style="background:@AvatarHelper.GetColorForUser(agent.Id)">@AvatarHelper.GetInitials(agent.Name)</span>
                <span>@agent.Name</span>
                @if (ActiveAgentIds.Contains(agent.Id) && agent.Id != SelectedAgentId)
                {
                    <span class="agent-activity-dot"></span>
                }
            </button>
        }
    </dialog>
}

@code {
    public enum SwitcherMode { Segmented, Popover }

    [Parameter] public List<AgentCatalogEntry> Agents { get; set; } = [];
    [Parameter] public string? SelectedAgentId { get; set; }
    [Parameter] public EventCallback<string> OnAgentSelected { get; set; }
    [Parameter] public SwitcherMode Mode { get; set; } = SwitcherMode.Segmented;
    [Parameter] public IReadOnlySet<string> ActiveAgentIds { get; set; } = new HashSet<string>();

    [Inject] private IJSRuntime Js { get; set; } = default!;
    private ElementReference _popover;

    private string SelectedAgentName => Agents.FirstOrDefault(a => a.Id == SelectedAgentId)?.Name ?? "Select agent";

    private async Task OpenPopover() => await Js.InvokeVoidAsync("hearthSheet.showDialog", _popover);

    private async Task Select(string agentId)
    {
        if (Mode == SwitcherMode.Popover)
        {
            await Js.InvokeVoidAsync("hearthSheet.closeDialog", _popover);
        }
        if (agentId != SelectedAgentId)
        {
            await OnAgentSelected.InvokeAsync(agentId);
        }
    }
}
```

- [ ] **Step 2: Add the `showDialog`/`closeDialog` interop helpers to `app.js`**

(These are also reused by the delete-confirm dialog in Task 6.) After the `accentHelper` block (added in Plan A), add:

```js
// ===================================
// Native <dialog> helpers
// ===================================

window.hearthSheet = window.hearthSheet || {};
window.hearthSheet.showDialog = function (el) { if (el && !el.open) el.showModal(); };
window.hearthSheet.closeDialog = function (el) { if (el && el.open) el.close(); };
```

- [ ] **Step 3: Build**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`
Expected: build succeeds. (`AgentSelector` is currently unused, so nothing else breaks yet — it's wired in by Task 2.)

- [ ] **Step 4: Commit**

```bash
git add WebChat.Client/Components/AgentSelector.razor WebChat.Client/wwwroot/app.js
git commit -m "feat(webchat): AgentSwitcher — segmented + popover modes with activity dots"
```

---

### Task 2: Rewrite `TopicList` as The Hearth (structure + bindings)

**Files:**
- Rewrite: `WebChat.Client/Components/TopicList.razor`
- Modify: `WebChat.Client/Components/Chat/ChatContainer.razor`

This task delivers the markup, bindings, and detent state — but the **resting** detent positions only (snap via class); the JS drag arrives in Tasks 4–5. The same DOM serves mobile (sheet) and desktop (rail) via CSS in Task 3.

- [ ] **Step 1: Rewrite `TopicList.razor`**

Replace the whole file. The `@code` block keeps the existing subscriptions and handlers; add `_detent`, `_search`, an `AgentActivityStore` subscription, and a `DotNetObjectReference` for the gesture/⌘K callbacks. Markup is the Hearth.

```razor
@inherits StoreSubscriberComponent
@implements IDisposable
@using WebChat.Client.Helpers
@using WebChat.Client.State.AgentActivity
@inject TopicsStore TopicsStore
@inject StreamingStore StreamingStore
@inject MessagesStore MessagesStore
@inject AgentActivityStore AgentActivityStore
@inject IDispatcher Dispatcher
@inject IJSRuntime Js

<div class="hearth @($"detent-{_detent.ToString().ToLowerInvariant()}")">
    <div class="hearth-peek" @ref="_peekBar">
        <button class="hearth-handle" aria-label="Expand conversations" @onclick="CycleDetent"></button>
        <div class="hearth-peek-current">
            <span class="hearth-peek-name @(SelectedIsStreaming ? "glow" : "")">@SelectedTopicName</span>
            <span class="hearth-peek-time">@SelectedTopicTime</span>
        </div>
        @if (AggregateUnread > 0)
        {
            <span class="topic-unread-badge">@(AggregateUnread > 99 ? "99+" : AggregateUnread)</span>
        }
    </div>

    <div class="hearth-body">
        <div class="hearth-rail-head">
            <AgentSelector Mode="AgentSelector.SwitcherMode.Segmented"
                           Agents="@_agents.ToList()" SelectedAgentId="@_selectedAgentId"
                           ActiveAgentIds="@_activeAgentIds"
                           OnAgentSelected="SelectAgent" />
        </div>

        <div class="hearth-search">
            <input class="hearth-search-input" type="search" placeholder="Search conversations"
                   @bind="_search" @bind:event="oninput" @ref="_searchInput" aria-label="Search conversations" />
        </div>

        <div class="hearth-rows">
            @{
                var rows = _topics
                    .Where(t => t.AgentId == _selectedAgentId)
                    .Where(t => string.IsNullOrWhiteSpace(_search) || t.Name.Contains(_search, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(t => t.LastMessageAt ?? t.CreatedAt)
                    .ToList();
            }
            @if (rows.Count == 0)
            {
                <div class="no-topics"><span>No conversations yet</span></div>
            }
            else
            {
                @foreach (var topic in rows)
                {
                    var unread = GetUnreadCount(topic.TopicId);
                    var streaming = IsTopicStreaming(topic.TopicId);
                    <div class="topic-item @(_selectedTopicId == topic.TopicId ? "selected" : "") @(unread > 0 ? "has-unread" : "") @(streaming ? "is-streaming" : "")"
                         @onclick="() => HandleTopicClick(topic)">
                        <div class="topic-content">
                            <div class="topic-name-row">
                                <div class="topic-name" data-tooltip="@topic.Name">@topic.Name</div>
                                @if (streaming)
                                {
                                    <div class="topic-streaming-indicator"><span></span><span></span><span></span></div>
                                }
                                @if (unread > 0)
                                {
                                    <div class="topic-unread-badge">@(unread > 99 ? "99+" : unread)</div>
                                }
                            </div>
                            <div class="topic-meta">@FormatTime(topic.LastMessageAt ?? topic.CreatedAt)</div>
                            <div class="topic-preview">@PreviewFor(topic.TopicId)</div>
                        </div>
                        @if (_deleteConfirmTopicId == topic.TopicId)
                        {
                            <div class="delete-confirm">
                                <button class="confirm-delete-btn" @onclick="() => ConfirmDelete(topic)" @onclick:stopPropagation="true" aria-label="Confirm delete">&#10003;</button>
                                <button class="cancel-delete-btn" @onclick="CancelDelete" @onclick:stopPropagation="true" aria-label="Cancel delete">&times;</button>
                            </div>
                        }
                        else
                        {
                            <button class="delete-btn" @onclick="() => ShowDeleteConfirm(topic.TopicId)" @onclick:stopPropagation="true" aria-label="Delete conversation">&times;</button>
                        }
                    </div>
                }
            }
        </div>
    </div>

    <div class="hearth-bottom">
        <AgentSelector Mode="AgentSelector.SwitcherMode.Popover"
                       Agents="@_agents.ToList()" SelectedAgentId="@_selectedAgentId"
                       ActiveAgentIds="@_activeAgentIds"
                       OnAgentSelected="SelectAgent" />
        <button class="hearth-new" @onclick="HandleNewTopic" aria-label="New chat">+</button>
    </div>
</div>

<div id="tooltip" class="tooltip"></div>

@code {
    private enum Detent { Peek, Half, Full }

    private IReadOnlyList<StoredTopic> _topics = [];
    private string? _selectedTopicId;
    private IReadOnlyList<AgentCatalogEntry> _agents = [];
    private string? _selectedAgentId;
    private IReadOnlySet<string> _streamingTopics = new HashSet<string>();
    private IReadOnlyDictionary<string, int> _unreadCounts = new Dictionary<string, int>();
    private IReadOnlySet<string> _activeAgentIds = new HashSet<string>();

    private string? _deleteConfirmTopicId;
    private string _search = "";
    private Detent _detent = Detent.Peek;

    private ElementReference _peekBar;
    private ElementReference _searchInput;
    private DotNetObjectReference<TopicList>? _selfRef;

    protected override void OnInitialized()
    {
        Subscribe(TopicsStore.StateObservable, s => s.Topics, t => _topics = t);
        Subscribe(TopicsStore.StateObservable, s => s.SelectedTopicId, id => _selectedTopicId = id);
        Subscribe(TopicsStore.StateObservable, s => s.Agents, a => _agents = a);
        Subscribe(TopicsStore.StateObservable, s => s.SelectedAgentId, id => _selectedAgentId = id);
        Subscribe(StreamingStore.StateObservable, s => s.StreamingTopics, ids => _streamingTopics = ids);
        Subscribe(MessagesStore.StateObservable,
            s => UnreadSelectors.ComputeUnreadCounts(s, TopicsStore.State, StreamingStore.State),
            c => _unreadCounts = c);
        Subscribe(TopicsStore.StateObservable,
            s => UnreadSelectors.ComputeUnreadCounts(MessagesStore.State, s, StreamingStore.State),
            c => _unreadCounts = c);
        Subscribe(StreamingStore.StateObservable,
            s => UnreadSelectors.ComputeUnreadCounts(MessagesStore.State, TopicsStore.State, s),
            c => _unreadCounts = c);
        Subscribe(AgentActivityStore.StateObservable,
            s => AgentActivitySelectors.GetAgentsWithActivity(s, StreamingStore.State),
            ids => _activeAgentIds = ids);
        Subscribe(StreamingStore.StateObservable,
            s => AgentActivitySelectors.GetAgentsWithActivity(AgentActivityStore.State, s),
            ids => _activeAgentIds = ids);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _selfRef = DotNetObjectReference.Create(this);
            await Js.InvokeVoidAsync("hearthSheet.register", _peekBar, _selfRef);
            await Js.InvokeVoidAsync("hearthSheet.registerCommandKey", _selfRef);
        }
    }

    [JSInvokable] public void CommitDetent(string detent)
    {
        _detent = Enum.Parse<Detent>(detent, ignoreCase: true);
        StateHasChanged();
    }

    [JSInvokable] public async Task OpenSearch()
    {
        _detent = Detent.Full;
        StateHasChanged();
        await Js.InvokeVoidAsync("hearthSheet.focus", _searchInput);
    }

    private void CycleDetent()
    {
        _detent = _detent switch
        {
            Detent.Peek => Detent.Half,
            Detent.Half => Detent.Full,
            _ => Detent.Peek
        };
    }

    private int GetUnreadCount(string topicId) => _unreadCounts.GetValueOrDefault(topicId);
    private bool IsTopicStreaming(string topicId) => _streamingTopics.Contains(topicId);
    private int AggregateUnread => _unreadCounts.Values.Sum();

    private StoredTopic? Selected => _topics.FirstOrDefault(t => t.TopicId == _selectedTopicId);
    private string SelectedTopicName => Selected?.Name ?? "Conversations";
    private string SelectedTopicTime => Selected is { } t ? FormatTime(t.LastMessageAt ?? t.CreatedAt) : "";
    private bool SelectedIsStreaming => _selectedTopicId is not null && _streamingTopics.Contains(_selectedTopicId);

    private string PreviewFor(string topicId)
    {
        var msgs = MessagesStore.State.MessagesByTopic.GetValueOrDefault(topicId, []);
        var last = msgs.LastOrDefault(m => !string.IsNullOrWhiteSpace(m.Content));
        var text = last?.Content ?? "";
        return text.Length > 80 ? text[..80] + "…" : text;
    }

    private static string FormatTime(DateTime utc) => utc.ToLocalTime().ToString("MMM d, h:mm tt");

    private void HandleTopicClick(StoredTopic topic)
    {
        if (_selectedTopicId != topic.TopicId) Dispatcher.Dispatch(new SelectTopic(topic.TopicId));
        _detent = Detent.Peek;
    }

    private void HandleNewTopic() => Dispatcher.Dispatch(new CreateNewTopic());
    private void ShowDeleteConfirm(string topicId) => _deleteConfirmTopicId = topicId;
    private void CancelDelete() => _deleteConfirmTopicId = null;

    private void ConfirmDelete(StoredTopic topic)
    {
        _deleteConfirmTopicId = null;
        Dispatcher.Dispatch(new RemoveTopic(topic.TopicId, topic.AgentId, topic.ChatId, topic.ThreadId));
    }

    private void SelectAgent(string agentId)
    {
        if (agentId != _selectedAgentId) Dispatcher.Dispatch(new SelectAgent(agentId));
    }

    public void Dispose() => _selfRef?.Dispose();
}
```

- [ ] **Step 2: Restructure `ChatContainer.razor` so the sheet overlays the chat on mobile**

Replace its body with the chat as the base layer and `TopicList` rendered after it (so the fixed sheet/rail composes correctly via CSS):

```razor
@page "/"
@page "/{Slug}"
@inherits StoreSubscriberComponent
@inject IDispatcher Dispatcher

<ApprovalModal />

<div class="chat-layout">
    <div class="chat-container">
        <MessageList />
        <ConnectionStatus />
        <div class="input-area">
            <ChatInput />
        </div>
    </div>

    <TopicList />
</div>

@code {
    [Parameter] public string? Slug { get; set; }
    private bool _initialized;

    protected override void OnParametersSet()
    {
        var slug = string.IsNullOrEmpty(Slug) ? "default" : Slug;
        Dispatcher.Dispatch(new SelectSpace(slug));
        if (!_initialized)
        {
            _initialized = true;
            Dispatcher.Dispatch(new Initialize());
        }
    }
}
```

- [ ] **Step 3: Build (expect it to compile; layout is wired by Task 3 CSS)**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`
Expected: build succeeds. (Visually the Hearth will be unstyled until Task 3 — that's expected.)

- [ ] **Step 4: Commit**

```bash
git add WebChat.Client/Components/TopicList.razor WebChat.Client/Components/Chat/ChatContainer.razor
git commit -m "feat(webchat): rewrite TopicList as The Hearth (structure + bindings)"
```

---

### Task 3: Hearth CSS — desktop rail + mobile sheet detents

**Files:**
- Modify: `WebChat.Client/wwwroot/css/app.css`

Delete the old sidebar/strip rules and add the Hearth styling. All colors use Plan A's tokens. **Detent heights, paddings, and shadows here are a complete starting point — tune against the running UI (spec §10).**

- [ ] **Step 1: Delete the obsolete rules**

Remove these rule blocks entirely:
- `.topic-sidebar { ... }` (and `.sidebar-header`, `.new-topic-btn` if sidebar-specific)
- `.topic-list { ... }` and its `::-webkit-scrollbar` variants
- inside `@media (max-width: 768px)`: the `.chat-layout { flex-direction: column }`, `.topic-sidebar { ... }`, `.sidebar-header`, `.topic-list { flex-direction: row; overflow-x: auto; ... }`, `.topic-item { min-width/max-width/flex-shrink }`, `.no-topics` row overrides — i.e. the entire horizontal-strip reflow.

Keep `.topic-item`, `.topic-name`, `.topic-meta`, `.topic-unread-badge`, `.topic-streaming-indicator`, `.delete-btn`, `.delete-confirm`, `.tooltip` (the Hearth rows reuse them).

- [ ] **Step 2: Add the Hearth base + desktop rail + mobile sheet CSS**

Append:

```css
/* ===================================
   The Hearth — navigation
   =================================== */

.hearth { display: flex; flex-direction: column; min-height: 0; }
.hearth-peek { display: none; }            /* mobile-only chrome */
.hearth-bottom { display: none; }          /* mobile-only chrome */

.hearth-body { display: flex; flex-direction: column; min-height: 0; flex: 1; }
.hearth-rail-head { padding: 0.75rem 0.75rem 0.5rem; }
.hearth-search { padding: 0 0.75rem 0.5rem; }
.hearth-search-input {
    width: 100%; padding: 0.5rem 0.75rem; border-radius: 10px;
    background: var(--bg-tertiary); border: 1px solid var(--border-color);
    color: var(--text-primary); font-family: inherit; font-size: 0.85rem;
}
.hearth-rows { flex: 1; overflow-y: auto; padding: 0 0.625rem 0.75rem; scroll-snap-type: y proximity; }
.topic-preview { font-size: 0.75rem; color: var(--text-muted); margin-top: 0.15rem;
    white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }

/* agent switcher — segmented (desktop) */
.agent-segmented { display: flex; gap: 0.25rem; background: var(--bg-tertiary);
    border-radius: 12px; padding: 0.25rem; }
.agent-seg { flex: 1; display: flex; align-items: center; gap: 0.4rem; justify-content: center;
    padding: 0.4rem 0.5rem; border: none; background: transparent; border-radius: 9px;
    color: var(--text-secondary); font-family: inherit; font-weight: 600; cursor: pointer; position: relative; }
.agent-seg.on { background: var(--accent); color: var(--user-text); }
.agent-mono { width: 22px; height: 22px; border-radius: 7px; display: grid; place-items: center;
    color: #fff; font-family: 'Fraunces', serif; font-weight: 600; font-size: 0.7rem; flex: 0 0 auto; }
.agent-activity-dot { width: 8px; height: 8px; border-radius: 50%; background: var(--accent);
    box-shadow: 0 0 8px 1px color-mix(in srgb, var(--accent) 70%, transparent); }
.is-streaming .agent-activity-dot, .agent-seg .agent-activity-dot { animation: emberPulse 1.6s ease-in-out infinite; }

@keyframes emberPulse { 0%,100% { opacity: 0.5; } 50% { opacity: 1; } }

.hearth-peek-name.glow { text-shadow: 0 0 10px color-mix(in srgb, var(--accent) 60%, transparent); }

/* Desktop: the Hearth IS a pinned left rail */
@media (min-width: 768px) {
    .chat-layout { display: flex; flex-direction: row; }
    .chat-container { order: 2; flex: 1; min-width: 0; }
    .hearth { order: 1; width: 320px; min-width: 320px; border-right: 1px solid var(--border-color);
        background: var(--bg-secondary); }
    .hearth-bottom { display: flex; align-items: center; gap: 0.5rem; padding: 0.6rem 0.75rem;
        border-top: 1px solid var(--border-color); }
    .hearth-new { margin-left: auto; width: 34px; height: 34px; border-radius: 10px; border: none;
        background: var(--accent); color: var(--user-text); font-size: 1.2rem; cursor: pointer; }
}

/* Mobile: the Hearth is a draggable bottom sheet */
@media (max-width: 767px) {
    .chat-layout { display: block; height: 100dvh; }
    .chat-container { height: 100dvh; padding-bottom: 56px; }   /* room for the peek bar */

    .hearth {
        position: fixed; left: 0; right: 0; bottom: 0; z-index: 40;
        height: 92dvh; background: var(--bg-secondary);
        border-top-left-radius: 18px; border-top-right-radius: 18px;
        box-shadow: 0 -10px 30px -12px rgba(60,40,20,0.35);
        transform: translateY(var(--sheet-offset, calc(92dvh - 56px)));
        transition: transform 280ms cubic-bezier(.2,.8,.2,1);
        touch-action: none;
    }
    .hearth.detent-peek  { --sheet-offset: calc(92dvh - 56px); }
    .hearth.detent-half  { --sheet-offset: 46dvh; }
    .hearth.detent-full  { --sheet-offset: 0dvh; }
    .hearth.dragging { transition: none; }   /* JS owns the transform during a drag */

    .hearth-peek { display: flex; align-items: center; gap: 0.5rem; height: 56px; padding: 0 0.875rem; }
    .hearth-handle { width: 36px; height: 4px; border-radius: 3px; background: var(--border-color);
        border: none; position: absolute; top: 7px; left: 50%; transform: translateX(-50%); padding: 0; }
    .hearth-peek-current { display: flex; flex-direction: column; min-width: 0; flex: 1; }
    .hearth-peek-name { font-family: 'Fraunces', serif; font-weight: 600; color: var(--text-primary);
        white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .hearth-peek-time { font-family: 'JetBrains Mono', monospace; font-size: 0.65rem; color: var(--text-muted); }
    .hearth-bottom { display: none; }   /* on mobile the agent chip + new chat live in the sheet head/bottom */

    .hearth-body { padding-bottom: env(safe-area-inset-bottom); }

    .agent-segmented { display: none; }   /* mobile uses the popover chip (rendered in .hearth-bottom on desktop) */
}

/* agent chip + popover (mobile) */
.agent-chip { display: flex; align-items: center; gap: 0.4rem; padding: 0.35rem 0.6rem; border-radius: 999px;
    background: var(--bg-tertiary); border: 1px solid var(--border-color); color: var(--text-primary);
    font-family: inherit; font-weight: 600; cursor: pointer; }
.agent-popover { border: none; border-radius: 16px; padding: 0.5rem; background: var(--bg-elevated);
    color: var(--text-primary); box-shadow: var(--shadow-lg); }
.agent-popover::backdrop { background: rgba(40,28,18,0.35); }
.agent-popover-item { display: flex; align-items: center; gap: 0.5rem; width: 100%; padding: 0.6rem 0.75rem;
    border: none; background: transparent; border-radius: 10px; color: var(--text-primary); font-family: inherit; cursor: pointer; }
.agent-popover-item.on { background: var(--accent-subtle); }
```

> On mobile the agent chip + new-chat need to be visible inside the sheet. Render `.hearth-bottom` inside the sheet on mobile by changing its `@media (max-width: 767px)` rule from `display: none` to a sticky bottom bar **if** QA shows the chip is needed at peek — otherwise the chip in `.hearth-rail-head` (the popover `AgentSelector`) suffices. Decide during Step 4 visual QA.

- [ ] **Step 3: Build**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`
Expected: build succeeds.

- [ ] **Step 4: Visual QA (desktop + mobile emulation)**

Run the app. Desktop (≥768px): a pinned ~320px left rail with segmented agent strip, search, two-line rows, new-chat. Mobile (devtools device emulation ≤767px): a peek bar at the bottom; tapping the handle cycles peek→half→full; the full detent shows search; tapping a row selects it and snaps to peek; the agent chip opens the popover `<dialog>`. (Smooth finger-drag arrives in Tasks 4–5; tap-cycle works now.)

- [ ] **Step 5: Commit**

```bash
git add WebChat.Client/wwwroot/css/app.css
git commit -m "feat(webchat): Hearth CSS — desktop rail + mobile sheet detents (snap)"
```

---

### Task 4: Gesture interop 6a — pointer plumbing (`--sheet-offset` via rAF)

**Files:**
- Modify: `WebChat.Client/wwwroot/app.js`

Live drag entirely in JS; no Blazor render mid-gesture.

- [ ] **Step 1: Add the `hearthSheet` drag core to `app.js`**

Extend the `window.hearthSheet` object (created in Task 1) with registration + pointer handling:

```js
window.hearthSheet = window.hearthSheet || {};
Object.assign(window.hearthSheet, {
    _el: null, _ref: null, _rows: null,
    _startY: 0, _startX: 0, _lastY: 0, _lastT: 0, _vy: 0, _dragging: false, _axisLocked: null,

    register: function (peekBar, dotnetRef) {
        const sheet = peekBar.closest('.hearth');
        if (!sheet) return;
        this._el = sheet;
        this._ref = dotnetRef;
        this._rows = sheet.querySelector('.hearth-rows');
        peekBar.addEventListener('pointerdown', this._onDown);
    },

    focus: function (el) { if (el) requestAnimationFrame(() => el.focus()); },

    _onDown: function (e) {
        const h = window.hearthSheet;
        h._startY = h._lastY = e.clientY;
        h._startX = e.clientX;
        h._lastT = e.timeStamp;
        h._vy = 0;
        h._axisLocked = null;
        h._dragging = true;
        h._el.classList.add('dragging');
        document.addEventListener('pointermove', h._onMove, { passive: false });
        document.addEventListener('pointerup', h._onUp);
    },

    _onMove: function (e) {
        const h = window.hearthSheet;
        if (!h._dragging) return;
        // Axis lock decided in Task 5; for 6a, treat all movement as vertical drag.
        const dy = e.clientY - h._startY;
        requestAnimationFrame(() => {
            const base = h._el.getBoundingClientRect().height; // ~92dvh
            const restPeek = base - 56;
            const offset = Math.min(restPeek, Math.max(0, restPeek + dy));
            h._el.style.setProperty('--sheet-offset', offset + 'px');
        });
        h._vy = (e.clientY - h._lastY) / Math.max(1, e.timeStamp - h._lastT);
        h._lastY = e.clientY;
        h._lastT = e.timeStamp;
        e.preventDefault();
    },

    _onUp: function () {
        const h = window.hearthSheet;
        h._dragging = false;
        h._el.classList.remove('dragging');
        document.removeEventListener('pointermove', h._onMove);
        document.removeEventListener('pointerup', h._onUp);
        h._settle();   // implemented in Task 6
    }
});
```

- [ ] **Step 2: Build & manual check**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`; run the app on a touch/mobile emulation. Dragging the peek bar now moves the sheet under your finger (it will snap back to its class detent on release until Task 6 wires `_settle`). Expected: smooth drag, no jank, no flicker (Blazor does not re-render during the drag).

- [ ] **Step 3: Commit**

```bash
git add WebChat.Client/wwwroot/app.js
git commit -m "feat(webchat): Hearth gesture 6a — pointer drag writes --sheet-offset via rAF"
```

---

### Task 5: Gesture interop 6b — two-axis lock + inner-scroll guard

**Files:**
- Modify: `WebChat.Client/wwwroot/app.js`

- [ ] **Step 1: Add axis-lock and inner-scroll handoff guard to `_onDown`/`_onMove`**

Replace `_onMove` (and add the guard in `_onDown`) so a drag only captures past a threshold, and never starts when the inner list is scrolled off its top:

```js
    _onDown: function (e) {
        const h = window.hearthSheet;
        // Don't start a sheet drag if the inner list is scrolled away from its top —
        // let the list scroll instead.
        if (h._rows && h._rows.scrollTop > 0 && h._rows.contains(e.target)) { h._dragging = false; return; }
        h._startY = h._lastY = e.clientY; h._startX = e.clientX; h._lastT = e.timeStamp;
        h._vy = 0; h._axisLocked = null; h._dragging = true;
        document.addEventListener('pointermove', h._onMove, { passive: false });
        document.addEventListener('pointerup', h._onUp);
    },

    _onMove: function (e) {
        const h = window.hearthSheet;
        if (!h._dragging) return;
        const dy = e.clientY - h._startY;
        const dx = e.clientX - h._startX;
        if (h._axisLocked === null) {
            const THRESH = 8;                       // px before we commit to an axis (tunable, spec §10)
            if (Math.abs(dy) < THRESH && Math.abs(dx) < THRESH) return;
            h._axisLocked = Math.abs(dy) >= Math.abs(dx) ? 'y' : 'x';
            if (h._axisLocked === 'y') h._el.classList.add('dragging');
        }
        if (h._axisLocked !== 'y') return;          // horizontal/ambiguous → ignore (no swipe-delete in v1)
        requestAnimationFrame(() => {
            const base = h._el.getBoundingClientRect().height;
            const restPeek = base - 56;
            const offset = Math.min(restPeek, Math.max(0, restPeek + dy));
            h._el.style.setProperty('--sheet-offset', offset + 'px');
        });
        h._vy = (e.clientY - h._lastY) / Math.max(1, e.timeStamp - h._lastT);
        h._lastY = e.clientY; h._lastT = e.timeStamp;
        e.preventDefault();
    },
```

- [ ] **Step 2: Build & manual check**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`; on mobile emulation confirm: a small move doesn't jump the sheet (threshold); horizontal swipes don't drag the sheet; scrolling the row list at full detent scrolls the list (no accidental sheet drag) and only drags the sheet when the list is at its top.

- [ ] **Step 3: Commit**

```bash
git add WebChat.Client/wwwroot/app.js
git commit -m "feat(webchat): Hearth gesture 6b — axis lock + inner-scroll handoff guard"
```

---

### Task 6: Gesture interop 6c — flick velocity → detent + release-commit

**Files:**
- Modify: `WebChat.Client/wwwroot/app.js`

- [ ] **Step 1: Implement `_settle` to pick a detent and commit to .NET**

Add to the `hearthSheet` object:

```js
    _settle: function () {
        const h = window.hearthSheet;
        if (h._axisLocked !== 'y') { h._el.style.removeProperty('--sheet-offset'); return; }
        const base = h._el.getBoundingClientRect().height;
        const current = parseFloat(getComputedStyle(h._el).getPropertyValue('--sheet-offset')) || (base - 56);
        const ratio = current / base;               // 0 = full, ~1 = peek
        const FLICK = 0.6;                          // px/ms threshold (tunable, spec §10)
        let detent;
        if (h._vy < -FLICK) detent = 'Full';
        else if (h._vy > FLICK) detent = 'Peek';
        else detent = ratio > 0.66 ? 'Peek' : ratio > 0.28 ? 'Half' : 'Full';
        h._el.style.removeProperty('--sheet-offset');   // let the .detent-* class drive the resting transform
        if (h._ref) h._ref.invokeMethodAsync('CommitDetent', detent);
    },
```

- [ ] **Step 2: Build & manual check**

Run: `dotnet build WebChat.Client/WebChat.Client.csproj`; on mobile emulation: a quick flick up snaps to full; flick down snaps to peek; a slow drag settles to the nearest detent; the chosen detent persists (the `detent-*` class is set via the `CommitDetent` JSInvokable, confirmed by the sheet staying put after release).

- [ ] **Step 3: Commit**

```bash
git add WebChat.Client/wwwroot/app.js
git commit -m "feat(webchat): Hearth gesture 6c — flick velocity to detent + release-commit"
```

---

### Task 7: ⌘K quick-switch + accessibility pass

**Files:**
- Modify: `WebChat.Client/wwwroot/app.js`

- [ ] **Step 1: Add the global ⌘K listener (guarded against the chat input)**

Add to the `hearthSheet` object:

```js
    registerCommandKey: function (dotnetRef) {
        document.addEventListener('keydown', function (e) {
            if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k'
                && !(e.target.classList && e.target.classList.contains('chat-input'))) {
                e.preventDefault();
                dotnetRef.invokeMethodAsync('OpenSearch');
            }
        });
    },
```

(`OpenSearch` is the `[JSInvokable]` added in Task 2 — it sets the Full detent and focuses the search field.)

- [ ] **Step 2: Accessibility verification (manual)**

Confirm: the agent popover (`<dialog>.showModal()`) traps focus, closes on Esc, and restores focus to the chip; the sheet handle is a real `<button>` with `aria-label` and cycles detents on Enter/Space; the segmented strip is a `radiogroup` with `aria-checked`; ⌘K opens search and focuses the field (and does NOT fire while typing in the chat input); with OS reduced-motion on (Plan A's guard), the sheet transition and ember pulse are suppressed. Fix any gaps (e.g. add `aria-expanded` to the handle reflecting `_detent != Peek`).

- [ ] **Step 3: Commit**

```bash
git add WebChat.Client/wwwroot/app.js
git commit -m "feat(webchat): Hearth ⌘K quick-switch + a11y pass"
```

---

### Task 8: E2E acceptance + final QA

**Files:**
- Create: `Tests/E2E/WebChat/HearthNavigationE2ETests.cs`

E2E requires Docker + `OPENROUTER__APIKEY`; tests are `[SkippableFact]`. Reuse the existing user/agent-selection helper from `WebChatE2ETests` (extract it to the fixture or a shared static if not already accessible).

- [ ] **Step 1: Write the E2E tests**

Create `Tests/E2E/WebChat/HearthNavigationE2ETests.cs`:

```csharp
using Microsoft.Playwright;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

[Trait("Category", "E2E")]
[Collection("WebChatE2E")]
public sealed class HearthNavigationE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task DesktopViewport_ShowsTheRail()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(1280, 900);
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        var rail = page.Locator(".hearth .agent-segmented");
        await rail.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000, State = WaitForSelectorState.Visible });
        (await rail.IsVisibleAsync()).ShouldBeTrue();
    }

    [SkippableFact]
    public async Task MobileViewport_ShowsPeekBarAndExpandsOnHandleTap()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(390, 844);
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // Peek bar present; rail segmented strip hidden on mobile.
        await page.Locator(".hearth-peek").WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        // Tapping the handle cycles to half/full and reveals the search field.
        await page.Locator(".hearth-handle").ClickAsync();
        await page.Locator(".hearth-handle").ClickAsync();
        await Assertions.Expect(page.Locator(".hearth-search-input")).ToBeVisibleAsync();
    }
}
```

(Note: `using Shouldly;` if `ShouldBeTrue()` is used; the repo's E2E tests use both Shouldly and Playwright `Assertions.Expect`.)

- [ ] **Step 2: Run the E2E tests (or skip if no stack)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HearthNavigationE2ETests"`
Expected: PASS, or `Skip` if Docker/`OPENROUTER__APIKEY` unavailable. To watch: `PLAYWRIGHT_HEADLESS=false dotnet test ...`.

- [ ] **Step 3: Final manual QA checklist (both themes, desktop + mobile)**

- Desktop rail: agent segmented strip switches agents one-tap; search filters rows; two-line preview shows; new-chat works; delete confirm works; selecting a topic loads it.
- Mobile sheet: peek shows current topic + glow when streaming; drag peek→half→full is smooth (no jank); flick up/down snaps; tap row → selects + snaps to peek; agent chip opens popover; ⌘K (with a keyboard) opens full + focuses search.
- Cross-agent dots: a background agent that is streaming (or just finished) shows the ember activity dot on its segmented button / popover item; the dot clears when you switch to it.
- Reduced-motion: transitions/pulses suppressed.
- No regression: the message send/receive flow still works (run `WebChatE2ETests.SendMessage_AppearsInChat`).

- [ ] **Step 4: Commit**

```bash
git add Tests/E2E/WebChat/HearthNavigationE2ETests.cs
git commit -m "test(webchat): E2E acceptance for The Hearth navigation"
```

---

## Self-Review

- **Spec coverage (Plan C scope = §5.1 mobile sheet, §5.2 desktop rail, §5.3 ⌘K + dots, §6.1 components, §6.4 CSS, §6.5 gesture + axis contract, §6.6 a11y):** AgentSwitcher (Task 1), Hearth structure + bindings (Task 2), rail+sheet CSS with old-rule deletion (Task 3), gesture 6a/6b/6c (Tasks 4–6), ⌘K + a11y (Task 7), E2E + QA (Task 8). Covered. Previews/recency honor §5.4 (recency = persisted `LastMessageAt`; preview = client-derived from `MessagesStore`).
- **Placeholder scan:** no "TBD"/"add styles here" — full markup, full CSS, full JS provided. Two explicit, bounded judgment calls are flagged for visual QA (whether the mobile agent chip needs a sticky bottom bar; exact `aria-expanded` wiring) rather than left vague; detent heights/thresholds are concrete values tagged as the spec-§10 tunables.
- **Type consistency:** `CommitDetent(string)` and `OpenSearch()` `[JSInvokable]` names match their `app.js` `invokeMethodAsync` callers; `hearthSheet.register`/`registerCommandKey`/`focus`/`showDialog`/`closeDialog`/`_settle` names are consistent across Tasks 1/4/5/6/7; the `AgentSelector` parameter set (`Mode`, `Agents`, `SelectedAgentId`, `ActiveAgentIds`, `OnAgentSelected`) matches its usages in `TopicList`; reused bindings (`SelectTopic`/`SelectAgent`/`CreateNewTopic`/`RemoveTopic`) match the verified action signatures.
- **Dependency check:** consumes Plan B's `UnreadSelectors`, `AgentActivityStore`, `AgentActivitySelectors` (all created there) and Plan A's tokens/reduced-motion. Build order A → B → C is required.
- **No-bUnit constraint honored:** no fictional component unit tests; acceptance is Playwright E2E (`[SkippableFact]`, the repo's real pattern) + manual QA, with the gesture/axis math verified by manual drag testing (it's untestable headless without a real pointer device).
