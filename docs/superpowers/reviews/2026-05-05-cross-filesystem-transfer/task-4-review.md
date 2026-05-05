# Task 4: Vault MCP tools (fs_copy, fs_blob_read, fs_blob_write)

## Spec Compliance
- Reviewer: ✅ Spec compliant. Three new wrappers (`FsCopyTool`, `FsBlobReadTool`, `FsBlobWriteTool`) under `McpServerVault/McpTools/`, each inheriting the matching domain base class via `(McpSettings settings)` primary constructor. `[McpServerToolType]`, `[McpServerTool(Name = "fs_*")]`, `[Description(Description)]` attributes correct. ConfigModule.cs appends three `.WithTools<>()` calls after `.WithTools<FsInfoTool>()`.

## Code Quality
- Reviewer: ✅ Approved.
- Pattern consistency verified against `FsCreateTool`, `FsReadTool`, `FsInfoTool` (the sync `McpSettings`-based sibling cluster). The base classes are sync so sync `McpRun` is correct (5 of 8 existing wrappers are sync).
- `length = MaxChunkSizeBytes` default arg compiles cleanly (BlobReadTool exposes it as `public const`).
- Build clean: 0 warnings, 0 errors.
- Full test suite: 1202/1202 pass after isolating two pre-existing LLM-flake tests confirmed independent of this change.
- Base SHA: fa15e12f96ae624009356c9eab600fc9601b7b53
- Head SHA: dd573fab0b24b1e6dc48f0b92d611844c4d6bda6

## Resolution
- Issues found: 2 minor (cosmetic registration order; exposed Domain const as wire-default)
- Issues fixed: 0 (deferred — purely cosmetic, no functional impact)
- Final status: ✅ Approved
