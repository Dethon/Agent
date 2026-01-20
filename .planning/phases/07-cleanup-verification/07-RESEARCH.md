# Phase 7: Cleanup and Verification - Research

**Researched:** 2026-01-20
**Domain:** Legacy code removal, test migration, verification
**Confidence:** HIGH

## Summary

This phase involves deleting two legacy classes (ChatStateManager, StreamingCoordinator) that have been replaced by the new store-based architecture, fixing pre-existing test failures, and ensuring test coverage is maintained or improved. The research confirms:

1. Both legacy classes are still referenced by production code and tests
2. The store-based replacements are fully functional (Phases 1-6 complete)
3. Two pre-existing test failures exist in StreamResumeServiceTests
4. Clear mapping exists between legacy functionality and store replacements

**Primary recommendation:** Delete ChatStateManager first (simpler, fewer dependencies), verify, then delete StreamingCoordinator. Adapt tests that exercise store-equivalent behavior; delete tests for truly obsolete internal methods.

## Standard Stack

This phase requires no new libraries. The work involves:

### Production Code to Delete
| File | Lines | Purpose | Replaced By |
|------|-------|---------|-------------|
| `WebChat.Client/Services/State/ChatStateManager.cs` | 272 | Centralized mutable state | TopicsStore, MessagesStore, StreamingStore, ApprovalStore |
| `WebChat.Client/Contracts/IChatStateManager.cs` | 63 | Interface for state manager | IDispatcher + Store subscriptions |
| `WebChat.Client/Services/Streaming/StreamingCoordinator.cs` | 417 | Stream processing, throttling | StreamingState + reducers, RenderCoordinator |
| `WebChat.Client/Contracts/IStreamingCoordinator.cs` | 21 | Interface for coordinator | Effect pattern + store actions |

### Test Files to Evaluate
| File | Tests | Decision Criteria |
|------|-------|-------------------|
| `Tests/Unit/WebChat/Client/ChatStateManagerTests.cs` | 69 | DELETE - Tests internal state mutations, all behavior covered by store tests |
| `Tests/Unit/WebChat/Client/StreamingCoordinatorTests.cs` | 28 | ADAPT - RebuildFromBuffer/StripKnownContent logic may need preservation |
| `Tests/Integration/WebChat/Client/StreamingCoordinatorIntegrationTests.cs` | 7 | ADAPT - E2E streaming tests, adapt to use stores directly |
| `Tests/Unit/WebChat/Client/StreamResumeServiceTests.cs` | 18 | FIX + MIGRATE - Fix 2 failures, migrate away from ChatStateManager |
| `Tests/Integration/WebChat/Client/StreamResumeServiceIntegrationTests.cs` | 5 | MIGRATE - Migrate away from ChatStateManager/StreamingCoordinator |

## Architecture Patterns

### Legacy Code Deletion Pattern

**Pattern:** Incremental deletion with verification
**Rationale:** Prevents cascading failures, enables easy rollback

```
1. Delete file
2. Build (expect compilation errors)
3. For each error:
   a. If reference can be removed -> remove it
   b. If functionality needed -> migrate to store equivalent
4. Build succeeds
5. Run tests
6. Fix test failures
7. Commit
8. Repeat for next file
```

### Test Migration Pattern

**Pattern:** Evaluate each test for value
**Rationale:** Tests for internal state mutations are obsolete; tests for behavior are valuable

```csharp
// BEFORE: Tests ChatStateManager internal state
[Fact]
public void StartStreaming_AddsToStreamingTopics()
{
    _stateManager.StartStreaming(topicId);
    _stateManager.IsTopicStreaming(topicId).ShouldBeTrue();
}

// AFTER: Tests StreamingStore behavior (ALREADY EXISTS)
[Fact]
public void StreamStarted_AddsTopicToStreamingTopics()
{
    _dispatcher.Dispatch(new StreamStarted("topic-1"));
    _store.State.StreamingTopics.ShouldContain("topic-1");
}
```

### Dead Code Detection Pattern

**Pattern:** Grep + IDE warnings
**Rationale:** Comprehensive coverage without specialized tooling

```bash
# Find all references to a type
grep -r "IChatStateManager\|ChatStateManager" --include="*.cs" | grep -v "^Tests/"

# Find unused private members (IDE handles this)
# IDE warnings: CS0169 (unused field), CS0414 (unused assignment), CS8618 (uninitialized)
```

## Don't Hand-Roll

Problems that look simple but have existing solutions:

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Finding dead code | Custom analyzer | grep + IDE warnings | IDE already tracks unused members |
| Test coverage measurement | Manual counting | `dotnet test --collect:"XPlat Code Coverage"` | Coverlet built into dotnet |
| Verifying test pass rate | Manual check | `dotnet test` exit code | Standard CI tooling |

## Common Pitfalls

### Pitfall 1: Deleting Code Still Referenced by Effects

**What goes wrong:** SendMessageEffect and StreamResumeService use IStreamingCoordinator
**Why it happens:** Effects bridge old and new architecture during migration
**How to avoid:** Check all Effects for legacy references before deletion
**Warning signs:** Build errors in State/Effects/ folder

### Pitfall 2: Breaking StreamResumeService Tests

**What goes wrong:** StreamResumeService currently depends on both IChatStateManager and IStreamingCoordinator
**Why it happens:** Service was partially migrated to use stores but still bridges to legacy
**How to avoid:** Fix the 2 pre-existing failures FIRST, then migrate service
**Warning signs:** Tests fail before any code deletion

### Pitfall 3: Losing RebuildFromBuffer Logic

**What goes wrong:** StreamingCoordinator contains complex buffer rebuild logic needed for reconnection
**Why it happens:** Logic is used by StreamResumeService for stream resumption
**How to avoid:** Either extract to utility class or inline into effect
**Warning signs:** Reconnection tests fail after deletion

### Pitfall 4: Incomplete Dead Code Sweep

**What goes wrong:** Types, methods, or parameters left orphaned after deletion
**Why it happens:** Grep finds explicit references but misses indirect ones
**How to avoid:** Build with warnings-as-errors, check IDE warnings post-deletion
**Warning signs:** IDE shows yellow squiggles for unused members

### Pitfall 5: Test Coverage Regression

**What goes wrong:** Delete tests for legacy code without verifying store tests cover same behavior
**Why it happens:** Assumption that store tests exist for all scenarios
**How to avoid:** Map each deleted test to corresponding store test or mark as truly obsolete
**Warning signs:** Coverage percentage drops significantly

## Code Examples

### Reference Mapping: ChatStateManager to Stores

```csharp
// BEFORE: ChatStateManager method
stateManager.AddTopic(topic);
stateManager.SelectTopic(topic);
stateManager.StartStreaming(topicId);
stateManager.SetApprovalRequest(request);

// AFTER: Store actions
dispatcher.Dispatch(new AddTopic(topic));
dispatcher.Dispatch(new SelectTopic(topic.TopicId));
dispatcher.Dispatch(new StreamStarted(topicId));
dispatcher.Dispatch(new ShowApproval(topicId, request));
```

### Current Service Dependencies

```csharp
// StreamResumeService dependencies (needs migration)
public sealed class StreamResumeService(
    IChatMessagingService messagingService,      // Keep
    ITopicService topicService,                  // Keep
    IChatStateManager stateManager,              // REMOVE - use stores
    IApprovalService approvalService,            // Keep
    IStreamingCoordinator streamingCoordinator,  // REMOVE - inline logic
    IDispatcher dispatcher,                      // Keep
    StreamingStore streamingStore)               // Keep
```

### Pre-existing Test Failure Analysis

```csharp
// Failure 1: TryResumeStreamAsync_LoadsHistoryIfNeeded
// Expected: messages.Count >= 2
// Actual: messages.Count == 1
// Root cause: StreamResumeService dispatches to MessagesStore but test reads from ChatStateManager
// Fix: Test should read from MessagesStore.State, not ChatStateManager

// Failure 2: TryResumeStreamAsync_StartsStreaming
// Expected: streamingStarted == true
// Actual: streamingStarted == false
// Root cause: Test subscribes to ChatStateManager.OnStateChanged but StreamResumeService
//             dispatches StreamStarted to stores, not ChatStateManager
// Fix: Subscribe to StreamingStore.StateObservable instead
```

### DI Registration Cleanup

```csharp
// BEFORE: Program.cs
builder.Services.AddScoped<IChatStateManager, ChatStateManager>();  // DELETE
builder.Services.AddScoped<IStreamingCoordinator, StreamingCoordinator>();  // DELETE

// AFTER: Only stores remain (already registered via AddWebChatStores())
builder.Services.AddWebChatStores();  // Keep - provides all state management
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| IChatStateManager | TopicsStore + MessagesStore + StreamingStore + ApprovalStore | Phase 1-2 | Unidirectional data flow |
| StreamingCoordinator throttling | RenderCoordinator.Throttle() | Phase 3 | Centralized, consistent |
| ChatStateManager.OnStateChanged | Store.StateObservable | Phase 1 | Rx.NET composability |
| Direct state mutation | Dispatcher.Dispatch(action) | Phase 1 | Immutable state, pure reducers |

## Open Questions

### 1. RebuildFromBuffer Preservation

**What we know:** StreamingCoordinator.RebuildFromBuffer() and StripKnownContent() are complex methods used for reconnection resumption
**What's unclear:** Should these be extracted to a utility class or inlined into StreamResumeService?
**Recommendation:** Extract to a static utility class for testability; logic is pure and has good test coverage

### 2. SendMessageEffect Dependency on IStreamingCoordinator

**What we know:** SendMessageEffect calls `_streamingCoordinator.StreamResponseAsync()`
**What's unclear:** Should streaming logic move into effect or remain as separate service?
**Recommendation:** Keep streaming as service (IStreamingService), just remove IStreamingCoordinator abstraction layer; complex async streaming logic benefits from dedicated class

### 3. Integration Test Scope

**What we know:** Integration tests verify full SignalR -> store -> component flow
**What's unclear:** How much manual verification is needed beyond automated tests?
**Recommendation:** Run all integration tests plus manual testing of: messaging, topic switching, reconnection, tool approval flow

## Sources

### Primary (HIGH confidence)
- Direct code inspection of WebChat.Client project
- Test execution results showing 2 pre-existing failures
- Phase 1-6 completion verification in STATE.md

### Secondary (MEDIUM confidence)
- CONTEXT.md decisions from /gsd:discuss-phase

### Tertiary (LOW confidence)
- None - all findings verified through code inspection

## Metadata

**Confidence breakdown:**
- Deletion targets: HIGH - verified via grep and file inspection
- Test migration: HIGH - analyzed each test file, clear categorization
- Pre-existing failures: HIGH - reproduced via test execution
- Dead code detection: MEDIUM - standard approach, may miss edge cases

**Research date:** 2026-01-20
**Valid until:** 2026-01-27 (one week, codebase is stable)
