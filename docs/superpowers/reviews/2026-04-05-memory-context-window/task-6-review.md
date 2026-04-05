# Task 6: MemoryRecallHook user window + anchor

## Spec Compliance
- Reviewer: ✅ Spec compliant. Interface, implementation, ChatMonitor, tests, and integration test all match plan. Stub comment and stub values from Task 5 are gone. All existing tests updated to 6-arg signature; 4 new behavior tests added matching plan verbatim. 11/11 `MemoryRecallHookTests` pass.

## Code Quality
- Reviewer: Approved with 1 Important + 4 Minor. 3 applied:
  - **Important (applied)**: `TryFetchThreadAsync` was returning `(null, stateKey)` on fetch failure, causing a spurious anchor=0 enqueue that would later extract from stale thread-start context. Fixed to `(null, null)` so the `stateKey is not null` guard suppresses enqueue.
  - **Minor (applied)**: `BuildRecallWindowText` rewritten with LINQ `.Append(currentText)` instead of mutating list; redundant `Count == 0` guard removed.
  - **Minor (applied)**: `WhenThreadStoreThrows` test now additionally asserts queue is empty — proves the Important fix.
  - Minor (declined): tightening `EnrichAsync_EnqueuesExtractionRequest` with `ThreadStateKey`/`AnchorIndex` assertions is redundant with the dedicated anchor test.
  - Minor (declined): null-text `.Where` guard — unlikely real-world issue, `string.Join` handles nulls gracefully.
- Base SHA: ec0a6d0b
- Head SHA: 464adaba

## Resolution
- Issues found: 5 (1 Important, 4 Minor)
- Issues fixed: 3
- Final status: ✅ Approved. 11/11 hook tests pass. Full memory test group clean. Pre-existing `ToolApprovalChatClientTests` flake unrelated (passes in isolation, file unchanged this task).
