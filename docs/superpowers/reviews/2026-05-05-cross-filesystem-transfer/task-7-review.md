# Task 7: Extend IFileSystemBackend + stub McpFileSystemBackend

## Spec Compliance
- Reviewer: ✅ Spec compliant. Three new method declarations added to `IFileSystemBackend` after `ExecAsync` exactly as specified. `McpFileSystemBackend.CopyAsync` is a faithful pass-through to `CallToolAsync("fs_copy", ...)` with argument keys matching the Vault MCP wrapper from Task 4. Two streaming methods stubbed with `NotImplementedException`.

## Code Quality
- Reviewer: ✅ Approved. `CopyAsync` mirrors `MoveAsync` byte-for-byte (style + naming). `Stream` resolves via implicit usings (`<ImplicitUsings>enable</ImplicitUsings>` in `Domain/Domain.csproj`). `Stream` is BCL — no Domain-layer purity violation. Stub form (`=> throw new NotImplementedException()`) is fail-fast, which is correct for stubs.
- Build clean: 0 warnings, 0 errors.
- Filtered test suite: 1179/1179 pass.
- Base SHA: bec6854c8690c52d983330bed5580256a8a37ef1
- Head SHA: d4940e16f6a5cab7dea6136aed569cebf6829f90

## Resolution
- Issues found: 0 critical/important; 1 minor (interface ordering — `CopyAsync` appended rather than slotted next to `MoveAsync`)
- Issues fixed: 0 (cosmetic only)
- Final status: ✅ Approved
