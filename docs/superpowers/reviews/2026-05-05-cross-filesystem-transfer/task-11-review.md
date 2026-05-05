# Task 11: VfsCopyTool - file source dispatch

## Spec Compliance
- Reviewer: ✅ Spec compliant. `VfsCopyTool` with `Key`/`Name`/`ToolDescription`, `RunAsync` (4 args + ct), `internal static TransferFileAsync` (with `deleteSource` flag for Task 12 reuse), `TransferDirectoryAsync` stubbed for Task 13. Same-FS branch delegates to native CopyAsync/MoveAsync; cross-FS branch streams via OpenReadStreamAsync → WriteFromStreamAsync.
- Documented deviation: tests use Moq instead of NSubstitute (Tests.csproj has Moq as the configured mocking dependency; no NSubstitute reference exists). Semantics preserved.

## Code Quality
- Reviewer: ⚠️ Approve with fixes → ✅ Approved (after one consistency iteration).
- Initial round flagged one important issue: non-uniform `bytes` field. Same-FS branch returned `null` if backend envelope omitted `bytes`; cross-FS always returned a long.
- Fix iteration: same-FS branch now coalesces to `-1L` when `bytes` is missing or non-numeric. Used `JsonValue.TryGetValue<long>(out var b) ? b : -1L` to handle Int32-serialized bytes from existing tests. Added regression test `RunAsync_SameFsFile_BackendOmitsBytes_ReturnsMinusOne`.
- Other issues deferred (envelope-shape divergence from `VfsMoveTool` was a deliberate spec choice; method length, ToolDescription forward reference to Task 13 best-effort, NotImplementedException stub — all minor).
- RED → GREEN: NullReferenceException on null cast → 3/3 passing.
- Base SHA: 0ad3b8ba7794b40154c9b7222620475986fdd818
- Head SHA: c2ddff1dd17d27afe293ffb45e40114cd4787ab2

## Resolution
- Issues found: 1 important (bytes-shape inconsistency) + several minor
- Issues fixed: 1 important + added 1 regression test
- Final status: ✅ Approved
