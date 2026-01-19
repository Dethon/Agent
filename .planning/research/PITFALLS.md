# Domain Pitfalls: Blazor State Management Refactoring

**Domain:** Blazor WebAssembly + SignalR state management refactoring
**Project Context:** Refactoring existing working WebChat client from scattered state to centralized stores
**Researched:** 2026-01-19

## Critical Pitfalls

Mistakes that cause rewrites, production bugs, or major functionality breakage.

### Pitfall 1: Event Handler Memory Leaks During Refactoring

**What goes wrong:** When refactoring from component-local state to centralized stores, developers add event subscriptions (e.g., `StateManager.OnStateChanged += handler`) but fail to maintain proper unsubscription in `Dispose()`. The existing codebase already has this pattern in `ChatContainer.razor` (lines 308-317), but refactoring may introduce new subscriptions without corresponding cleanup.

**Why it happens:**
- New store classes introduce new `OnStateChanged` events
- Copy-paste of subscription code without the matching Dispose logic
- Refactoring moves subscription setup to different lifecycle methods

**Consequences:**
- Memory leaks accumulate as users navigate between topics
- Callbacks fire on disposed components causing exceptions
- Application becomes sluggish after extended use

**Prevention:**
1. Create a checklist: every `+=` subscription MUST have a corresponding `-=` in Dispose
2. Use named handler methods (not lambdas) so they can be unsubscribed
3. Consider a subscription manager pattern that auto-disposes

**Detection (warning signs):**
- Memory usage climbs steadily during session
- `ObjectDisposedException` in console
- Duplicate state updates after navigation

**Existing code reference:**
```csharp
// Current pattern in ChatContainer.razor - PRESERVE THIS
public void Dispose()
{
    if (_stateChangedHandler is not null)
    {
        StateManager.OnStateChanged -= _stateChangedHandler;
    }
    ConnectionService.OnStateChanged -= StateHasChanged;
    // ...
}
```

**Phase relevance:** Phase 1 (Foundation) - Establish subscription patterns before adding new stores

---

### Pitfall 2: InvokeAsync(StateHasChanged) Anti-Pattern

**What goes wrong:** Developers use `InvokeAsync(StateHasChanged)` to suppress threading exceptions when SignalR callbacks update state, but this hides race conditions rather than fixing them. The current `StreamingCoordinator.cs` uses a render callback pattern that may tempt developers to simplify incorrectly.

**Why it happens:**
- SignalR hub callbacks execute on non-UI threads
- `StateHasChanged` throws if called from wrong thread
- `InvokeAsync(StateHasChanged)` suppresses the exception but doesn't fix the underlying race

**Consequences:**
- Race conditions cause duplicate messages, lost updates, or corrupted state
- Bugs manifest intermittently, making them hard to reproduce
- State becomes inconsistent between what user sees and what's stored

**Prevention:**
1. Wrap ALL state-mutating code (not just StateHasChanged) inside InvokeAsync
2. Keep state mutations atomic - don't await between read and write
3. Use the existing throttled render pattern from `StreamingCoordinator.ThrottledRenderAsync`

**Detection (warning signs):**
- Intermittent duplicate messages in UI
- State diverges between components showing same data
- Console shows threading-related warnings

**Correct pattern:**
```csharp
// WRONG - hides race condition
async Task OnHubMessage(string msg)
{
    _messages.Add(msg);  // Race condition here!
    await InvokeAsync(StateHasChanged);
}

// CORRECT - all state access synchronized
async Task OnHubMessage(string msg)
{
    await InvokeAsync(() =>
    {
        _messages.Add(msg);  // Now protected
        StateHasChanged();
    });
}
```

**Phase relevance:** Phase 2 (Store Implementation) - Critical when connecting stores to SignalR events

---

### Pitfall 3: Streaming State Loss During Reconnection

**What goes wrong:** Refactoring the streaming state management breaks the carefully designed reconnection logic in `StreamResumeService.cs`. The current implementation tracks `_resumingTopics` to prevent duplicate resume attempts and deduplicates content against history.

**Why it happens:**
- New centralized store doesn't preserve all the intermediate state flags
- Simplification removes "defensive" code that seems unnecessary
- Testing doesn't cover reconnection scenarios

**Consequences:**
- Users see duplicate messages after reconnect
- Streaming content lost when browser goes to sleep
- Partial messages appear as complete

**Prevention:**
1. Map every field in current `ChatStateManager` to new store design before coding
2. Preserve `TryStartResuming`/`StopResuming` guards - they prevent race conditions
3. Test reconnection explicitly: disconnect WiFi, wait, reconnect

**Detection (warning signs):**
- `TryStartResuming` returns false unexpectedly
- Same message appears twice after reconnect
- StreamResumeService tests fail

**Critical state to preserve:**
```csharp
// These MUST migrate to new stores - don't lose any
private readonly HashSet<string> _streamingTopics = new();
private readonly HashSet<string> _resumingTopics = new();
private readonly Dictionary<string, ChatMessageModel?> _streamingMessageByTopic = new();
```

**Phase relevance:** Phase 3 (Migration) - When moving StreamingCoordinator to use new stores

---

### Pitfall 4: Re-render Cascade During Streaming

**What goes wrong:** Centralized state changes trigger re-renders in ALL subscribed components, not just the affected one. With streaming at 50+ updates/second, this causes UI freeze or excessive CPU usage.

**Why it happens:**
- Flux/Redux patterns broadcast all state changes to all subscribers
- Components don't filter which state changes they care about
- `NotifyStateChanged()` is called for every streaming chunk

**Consequences:**
- UI freezes during streaming
- Browser becomes unresponsive
- Battery drain on mobile devices

**Prevention:**
1. Keep the existing `UpdateStreamingMessage` pattern that does NOT call `NotifyStateChanged()`
2. Use selective subscriptions - components subscribe to specific state slices
3. Maintain the `ThrottledRenderAsync` pattern (50ms throttle) for streaming updates
4. Split state into separate stores: `TopicStore`, `StreamingStore`, `UIStore`

**Detection (warning signs):**
- UI lag increases during streaming
- CPU spikes visible in browser dev tools
- `StateHasChanged` called 100+ times per second

**Current protection (preserve this):**
```csharp
// From ChatStateManager.cs line 156-157
public void UpdateStreamingMessage(string topicId, ChatMessageModel? message)
{
    _streamingMessageByTopic[topicId] = message;
    // Note: NotifyStateChanged is NOT called here to allow throttled rendering
}
```

**Phase relevance:** Phase 2 (Store Implementation) - Design stores with selective notification

---

### Pitfall 5: Breaking Existing Tests During Refactor

**What goes wrong:** Existing tests in `Tests/Unit/WebChat/Client/` rely on the current `ChatStateManager` and `StreamingCoordinator` APIs. Refactoring changes these APIs, breaking tests without providing equivalent coverage.

**Why it happens:**
- Desire to "clean up" old patterns while refactoring
- New store API differs from old state manager API
- Tests are seen as implementation-specific rather than behavior-specific

**Consequences:**
- Regression bugs introduced without test coverage
- Loss of documented behavior (tests ARE documentation)
- False confidence in refactored code

**Prevention:**
1. Run ALL existing tests before starting refactor - they must pass
2. Preserve test coverage by adapting tests, not deleting them
3. Add adapter layer if needed to maintain API compatibility
4. Write new tests for new store patterns BEFORE implementation

**Detection (warning signs):**
- Test count decreases after refactor
- Tests marked as `[Skip]` or commented out
- "I'll fix the tests later" commits

**Critical tests to preserve:**
- `ChatStateManagerTests.cs` - 60+ test methods covering state transitions
- `StreamingCoordinatorTests.cs` - Buffer rebuilding, deduplication, multi-turn handling
- `StreamResumeServiceTests.cs` - Reconnection scenarios

**Phase relevance:** All phases - Tests are the safety net

---

## Moderate Pitfalls

Mistakes that cause delays, technical debt, or degraded user experience.

### Pitfall 6: Two-Way Binding Conflicts with Immutable State

**What goes wrong:** Blazor forms and inputs expect two-way binding (`@bind`), but Flux patterns require immutable state updated only through actions. Mixing these patterns causes confusing behavior.

**Why it happens:**
- Existing code uses mutable models (`ChatMessageModel` with settable properties)
- Input fields naturally expect `@bind`
- Flux state should be read-only

**Prevention:**
1. Use local component state for form input values
2. Dispatch action only on explicit user action (submit, blur)
3. Don't bind directly to store state

**Phase relevance:** Phase 2 (Store Implementation) - When designing action patterns

---

### Pitfall 7: Over-Engineering Initial Store Structure

**What goes wrong:** Developers create complex multi-store architecture before understanding which state actually needs to be shared, leading to excessive boilerplate and difficult maintenance.

**Why it happens:**
- Redux tutorials show complex patterns for large apps
- Desire to "do it right the first time"
- Not analyzing current state access patterns

**Prevention:**
1. Map current state access patterns in `ChatContainer.razor` first
2. Start with ONE store, split only when specific pain points emerge
3. Keep action/reducer count minimal initially

**Phase relevance:** Phase 1 (Foundation) - Resist premature optimization

---

### Pitfall 8: Losing Approval Flow During Refactor

**What goes wrong:** The tool approval modal (`ApprovalModal.razor`) relies on `CurrentApprovalRequest` state that must be set during streaming and cleared after response. Refactoring can break this flow.

**Why it happens:**
- Approval state crosses multiple concerns (streaming, UI, SignalR)
- Easy to miss one of the state transitions
- Modal visibility depends on state being set correctly

**Prevention:**
1. Document the approval flow before refactoring:
   - SignalR receives approval request
   - `SetApprovalRequest(request)` called
   - Modal renders
   - User responds
   - `SetApprovalRequest(null)` clears
2. Test approval flow end-to-end after refactor

**Phase relevance:** Phase 3 (Migration) - When moving approval state to new stores

---

### Pitfall 9: Throttle Logic Duplication

**What goes wrong:** Multiple components implement their own throttling for SignalR updates, leading to inconsistent behavior and duplicate logic.

**Why it happens:**
- Different components need throttled updates
- Copy-paste of throttle code
- No centralized throttling service

**Current implementation:**
```csharp
// StreamingCoordinator.cs - single throttle implementation
private DateTime _lastRenderTime = DateTime.MinValue;
private const int RenderThrottleMs = 50;
private readonly Lock _throttleLock = new();
```

**Prevention:**
1. Keep throttling in `StreamingCoordinator` (or equivalent service)
2. Don't add throttling to individual stores
3. Pass render callbacks to coordinator, don't have stores render directly

**Phase relevance:** Phase 2 (Store Implementation) - Decide throttling ownership early

---

### Pitfall 10: SignalR Event Subscription Ordering

**What goes wrong:** After refactor, SignalR event handlers run before stores are initialized or after stores are disposed, causing null reference exceptions.

**Why it happens:**
- `OnInitializedAsync` timing differs from service construction
- SignalR reconnection can fire events during component disposal
- Async initialization race conditions

**Current protection:**
```csharp
// ChatContainer.razor - subscription happens in OnInitializedAsync
protected override async Task OnInitializedAsync()
{
    // State manager already exists (scoped service)
    _stateChangedHandler = () => InvokeAsync(StateHasChanged);
    StateManager.OnStateChanged += _stateChangedHandler;
    // ...
}
```

**Prevention:**
1. Use null-conditional operators on event invocation
2. Guard against actions on disposed components
3. Consider component lifecycle state machine

**Phase relevance:** Phase 3 (Migration) - When rewiring SignalR to new stores

---

## Minor Pitfalls

Annoyances that are fixable without major rework.

### Pitfall 11: Inconsistent State Shape Between Stores

**What goes wrong:** Different stores use different conventions for representing similar data (e.g., nullable vs. empty collection, string vs. enum for status).

**Prevention:**
- Define shared types in Domain layer
- Use records for immutable state objects
- Document state shape conventions in ARCHITECTURE.md

---

### Pitfall 12: Missing StateHasChanged After Store Update

**What goes wrong:** Component updates store but forgets to trigger re-render, leaving UI stale.

**Prevention:**
- Store automatically calls NotifyStateChanged (current pattern)
- Or components use `StateHasChanged` via subscription
- Don't rely on manual StateHasChanged calls after every store method

---

### Pitfall 13: DevTools Not Configured for Debugging

**What goes wrong:** Without Redux DevTools integration, debugging state flow is difficult. Developers can't inspect action history or time-travel debug.

**Prevention:**
- If using Fluxor, add `Fluxor.Blazor.Web.ReduxDevTools` package
- If custom implementation, log actions in development mode
- Include action names and payloads in logs

---

## Phase-Specific Warnings

| Phase | Likely Pitfall | Mitigation |
|-------|---------------|------------|
| 1. Foundation | Pitfall 7 (Over-engineering) | Start simple, one store |
| 1. Foundation | Pitfall 5 (Breaking tests) | All tests pass before starting |
| 2. Store Implementation | Pitfall 4 (Re-render cascade) | Selective notifications from start |
| 2. Store Implementation | Pitfall 2 (InvokeAsync anti-pattern) | Wrap ALL state mutations |
| 3. Migration | Pitfall 3 (Streaming state loss) | Map every state field first |
| 3. Migration | Pitfall 1 (Memory leaks) | Subscription audit checklist |
| 3. Migration | Pitfall 8 (Approval flow) | End-to-end test before/after |
| 4. Cleanup | Pitfall 5 (Test coverage) | Verify test count unchanged |

## Checklist Before Each Phase

### Before Phase 1 (Foundation)
- [ ] All existing tests pass (run `dotnet test`)
- [ ] Current state access patterns documented
- [ ] Subscription/dispose pairs identified in existing code

### Before Phase 2 (Store Implementation)
- [ ] Store boundaries decided based on Phase 1 analysis
- [ ] Notification strategy defined (global vs. selective)
- [ ] Throttling ownership assigned (keep in StreamingCoordinator)

### Before Phase 3 (Migration)
- [ ] Complete state field mapping (old -> new)
- [ ] Reconnection scenarios documented
- [ ] Approval flow documented step-by-step

### Before Phase 4 (Cleanup)
- [ ] All tests pass
- [ ] No memory leaks in 10-minute usage test
- [ ] Streaming performs as well as before refactor

## Sources

- [Microsoft Learn: Blazor Component Disposal](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/component-disposal)
- [Microsoft Learn: Blazor Rendering Performance](https://learn.microsoft.com/en-us/aspnet/core/blazor/performance/rendering)
- [Microsoft Learn: Blazor Synchronization Context](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/synchronization-context)
- [Blazor University: InvokeAsync Thread Safety](https://blazor-university.com/components/multi-threaded-rendering/invokeasync/)
- [Blazor University: Memory Leaks](https://blazor-university.com/javascript-interop/calling-dotnet-from-javascript/lifetimes-and-memory-leaks/)
- [Blazor Server Memory Management](https://amarozka.dev/blazor-server-memory-management-circuit-leaks/)
- [Infragistics: Blazor State Management Best Practices](https://www.infragistics.com/blogs/blazor-state-management/)
- [Code Maze: Using Fluxor in Blazor](https://code-maze.com/fluxor-for-state-management-in-blazor/)
- [Telerik: Blazor Complex State Scenarios](https://www.telerik.com/blogs/blazor-basics-dealing-complex-state-scenarios-blazor)
- [Medium: My Component Re-rendered 20 Times](https://medium.com/careerbytecode/my-blazor-component-re-rendered-20-times-heres-why-and-how-i-fixed-it-045467fb0b33)
- [GitHub Issue: FluentUI InvokeAsync Anti-Pattern](https://github.com/microsoft/fluentui-blazor/issues/506)
- [bUnit: Testing Blazor Components](https://bunit.dev/docs/getting-started/writing-tests.html)
