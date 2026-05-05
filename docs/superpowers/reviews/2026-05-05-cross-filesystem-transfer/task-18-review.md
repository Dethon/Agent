# Task 18: Cross-FS directory move integration test

## Spec Compliance
- Reviewer: ✅ Spec compliant. `Tests/Integration/Domain/Tools/FileSystem/VfsMoveToolIntegrationTests.cs` adds `[Fact] RunAsync_CrossFsDirectory_MovesAllFilesAndRemovesSource`. Seeds `project/a.md` + `project/sub/b.md` on library, moves `/library/project` → `/notes/project`, asserts `status="ok"`, `summary.transferred==2`, source files gone, destination files present with original content. Matches the spec snippet verbatim (modulo a trivial helper-method refactor: `Connect`/`BuildRegistry` extracted, `await using` for client disposal — both improvements over the literal spec). Spec did NOT mandate `entries[]` assertions, so their absence is not a gap.
- Out-of-scope production fixes shipped in same commit:
  1. `VfsMoveTool`/`VfsCopyTool`: `info["type"] == "directory"` → `info["isDirectory"]?.GetValue<bool>() == true`. Real production bug — `FsInfoTool` returns `{isDirectory: bool}`, so the directory branch was dead code in cross-FS calls. Legitimate fix; required to make Task 18's test pass against the real backend.
  2. `VfsCopyTool.TransferDirectoryAsync`: tail extraction via new `ExtractTail` helper instead of `srcRel.StartsWith(src.RelativePath)`. Real bug — `Matcher.GetResultsInFullPath` returns absolute paths. Legitimate fix.
  3. `MultiFileSystemFixture`: registers `FsInfoTool`. Necessary infra; without it, no cross-FS integration test exercising directory dispatch could pass.
- Verdict on scope: all three fixes are bug fixes surfaced by the integration test, not feature additions. Acceptable to bundle with Task 18, but worth flagging that **unit-test mocks in `Tests/Unit/Domain/Tools/FileSystem/VfsCopyToolTests.cs` (lines 17, 43, 71) and `VfsMoveToolCrossFsTests.cs` (lines 18, 48) still use `["type"] = "file"`**. They no longer match the production contract and only continue to pass because the directory branch (now correctly gated on `isDirectory`) is never entered. These mocks should be updated to `["isDirectory"] = false` for canonical contract alignment — minor follow-up, not blocking.

## Code Quality
- Reviewer: ✅ Approved. New test mirrors `VfsCopyToolIntegrationTests` exactly: `[Collection("MultiFileSystem")]`, primary-constructor injection, identical `Connect`/`BuildRegistry` helpers, `await using` McpClient disposal, Shouldly assertions. Both `info["isDirectory"]?.GetValue<bool>() == true` updates align with the canonical `FsInfoTool` shape (`Domain/Tools/Files/FileInfoTool.cs:42,52`) — null-safe, value-type-correct, dead-code-eliminating fix. `ExtractTail` is single-responsibility, pure, static, < 25 lines, three clear early-return branches (empty dir / prefix match / interior marker fallback to `Path.GetFileName`). `Replace('\\', '/')` provides defensive Windows-path handling. Fence-post correctness verified for all observed glob shapes (relative `project/a.md`, absolute `/project/a.md`). No dead code, no unnecessary allocation in hot paths, no missing guards. Minor: `Connect`/`BuildRegistry` are now duplicated across `VfsCopyToolIntegrationTests` and `VfsMoveToolIntegrationTests` — could hoist into the fixture as a helper, but matches the established pattern from Task 16/17 so consistency wins. Build clean (0 warnings).
- GREEN: 13/13 passing for `VfsCopyTool|VfsMoveTool|McpFileSystemBackend` filter.
- Base SHA: 6dc8893b7126a6d5d82554ff9fb9274fc892fb20
- Head SHA: de789189f30c8147f725b56fda2d2e679d98d2c6

## Resolution
- Issues found: 1 minor (stale `["type"] = "file"` mocks in two unit-test files, contract drift)
- Issues fixed: 0 (deferred — non-blocking, recommend follow-up commit)
- Final status: ✅ Approved
