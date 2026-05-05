# Task 12: Extend VfsMoveTool with cross-FS

## Spec Compliance
- Reviewer: ✅ Spec compliant. `VfsMoveTool.RunAsync` body replaced with delegation to `VfsCopyTool.TransferFileAsync`/`TransferDirectoryAsync` with `deleteSource: true`. Cross-FS rejection envelope removed. New `overwrite`/`createDirectories` parameters added with backward-compatible defaults. Description updated to reflect the new capability + non-atomic warning.
- Updated existing same-FS test (added `InfoAsync` setup, asserts on new envelope shape via `Verify`). Deleted obsolete `RunAsync_DifferentFilesystems_ReturnsCrossFilesystemError`.

## Code Quality
- Reviewer: ✅ Approved. Clean delegation symmetric with `VfsCopyTool.RunAsync` (only `deleteSource` differs). New test class covers cross-FS streaming + same-FS native delegation. No straggler `CrossFilesystem` references in tests.
- Build clean. RED → GREEN: NullReferenceException on absent `status` field → all VfsMoveTool tests passing.
- Base SHA: c2ddff1dd17d27afe293ffb45e40114cd4787ab2
- Head SHA: 7d5094d78ab452f44949219bb6bfb8b1e9df693b

## Resolution
- Issues found: 0 critical/important; minor (slight test redundancy between same-FS coverage in old vs new file; unused `ToolError.Codes.CrossFilesystem` constant deferred for later cleanup; pre-existing `MoveToolTests` class-name vs file-name mismatch)
- Issues fixed: 0 (deferred — cosmetic/scope concerns)
- Final status: ✅ Approved
