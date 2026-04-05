# Task 5: MemoryExtractionRequest shape + worker windowing

## Spec Compliance
- Reviewer: Initial review found 1 deviation — `WithMissingThread` test was missing the zero-metric publish assertions from the plan. Fixed in a follow-up commit. All other 8 requirements met (DTO shape, worker constructor position, slice algorithm, enqueue stub, DI wiring, appsettings, queue tests, recall test). `ChatMonitor` and `IMemoryRecallHook` untouched as expected. `MemoryDtosTests.cs` pre-existed and was updated only for signature fixes.

## Code Quality
- Reviewer: Approved with 2 Minor recommendations (unreachable empty-window guard; stub comment style in MemoryRecallHook). Both explicitly non-blocking.
- Base SHA: c023c234
- Head SHA: ec0a6d0b

## Resolution
- Issues found: 3 (1 spec deviation + 2 Minor code-quality)
- Issues fixed: 1 (spec deviation). 2 Minor kept as-is:
  - Empty-window guard is defensive against `WindowMixedTurns ≤ 0` misconfig and matches the plan's prescribed code verbatim.
  - Stub comment in `MemoryRecallHook` will be removed entirely in Task 6 which rewrites that call site.
- Final status: ✅ Approved. 1092/1092 unit tests pass.
