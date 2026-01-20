# Phase 7: Cleanup and Verification - Context

**Gathered:** 2026-01-20
**Status:** Ready for planning

<domain>
## Phase Boundary

Remove legacy code (ChatStateManager, StreamingCoordinator) that has been replaced by the new store architecture. Verify all WebChat functionality works correctly. Ensure test coverage is maintained or improved. This phase completes the refactoring.

</domain>

<decisions>
## Implementation Decisions

### Deletion strategy
- Delete one file at a time: ChatStateManager first, then StreamingCoordinator
- After each deletion, run build + tests before proceeding to next
- Git history is sufficient backup — no archive folder needed
- For each compilation error: Claude evaluates whether to remove the reference or migrate to store calls

### Verification approach
- Both automated tests AND manual verification required
- Manual testing covers full feature set: messaging, topics, reconnection, tool approval
- Performance comparison required: measure streaming responsiveness before vs after
- Any issues found during verification must be fixed immediately (not deferred)

### Dead code detection
- Full codebase sweep across WebChat projects
- Comprehensive cleanup: types, methods, fields, parameters, unused using statements
- Delete aggressively — if no reference in codebase, it's dead
- Claude chooses detection approach (IDE warnings, grep, or combination)

### Test coverage handling
- Goal: test coverage percentage same or better than before refactoring
- Fix pre-existing failing tests in StreamResumeServiceTests as part of this phase
- For deleted code's tests: Claude evaluates each test — adapt valuable ones to test store behavior, delete truly obsolete ones
- Coverage includes unit tests for stores AND integration tests for SignalR -> store -> component flow

### Claude's Discretion
- Order of deletion within the "one at a time" constraint
- Specific dead code detection tooling/approach
- Which tests to adapt vs delete
- Integration test scope and depth

</decisions>

<specifics>
## Specific Ideas

- StreamResumeServiceTests has 2 pre-existing failures that should be fixed in this phase
- STATE.md notes accumulated TODOs that may be addressed here if relevant to cleanup

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope

</deferred>

---

*Phase: 07-cleanup-verification*
*Context gathered: 2026-01-20*
