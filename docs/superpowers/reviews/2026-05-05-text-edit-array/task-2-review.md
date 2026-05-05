# Tasks 2-11: Refactor TextEditTool to array signature

## Spec Compliance
- Reviewer: ✅ Spec compliant — all 10 plan tasks (2-11) verified.
  - Task 2: 13 facts in `Tests/Unit/Domain/Text/TextEditToolTests.cs`, `TestableTextEditTool.TestRun` accepts `IReadOnlyList<TextEdit>`.
  - Task 3: `protected JsonNode Run(string filePath, IReadOnlyList<TextEdit> edits)`, null-check + empty-array guard, sequential apply against in-memory string, atomic temp+rename write, result JSON has `status/filePath/totalOccurrencesReplaced/edits[].occurrencesReplaced/edits[].affectedLines.{start,end}`. `StringComparison.Ordinal` throughout.
  - Task 4: Both `FsEditTool` wrappers take `(string path, IReadOnlyList<TextEdit> edits)`, no try/catch, `[McpServerTool(Name = "fs_edit")]` preserved.
  - Task 5: `IFileSystemBackend.EditAsync(string, IReadOnlyList<TextEdit>, CancellationToken)`; `McpFileSystemBackend.EditAsync` serializes edits as `{oldString, newString, replaceAll}` dict list.
  - Task 6: `VfsTextEditTool.RunAsync` resolves path once via local variable.
  - Task 7: 2 facts in `Tests/Unit/Domain/Tools/FileSystem/VfsTextEditToolTests.cs`, class named `TextEditToolTests` per plan.
  - Task 8: 15 unit tests pass.
  - Task 9: 17 integration tests pass.
  - Task 10: No 4-arg `EditAsync` stragglers.
  - Task 11: Single commit `283aaaac` with exactly the 8 plan-listed files. Commit message verbatim.
  - Task 1 commit `c3fad43d` untouched.

## Code Quality
- Reviewer: Approved (with minor advisory items beyond plan scope).
- Strengths: True atomicity verified (tested via mid-sequence failure case); sequential chaining real (later edit sees earlier edit's output, tested); temp + rename idiom; no try/catch in MCP tool methods; file-scoped namespaces, primary constructors, `record` DTO; comprehensive tests covering ordering, chaining, mid-failure, empty-array guard, total-occurrences summation; `[Description]` attributes on LLM-facing parameters.
- Advisory (non-blocking):
  - `FsEditTool.McpRun` parameters omit a `[Description]` on `edits` (consistent with the plan's verbatim code, but inconsistent with `VfsTextEditTool.RunAsync` which has one). Future work.
  - No coverage for `affectedLines` correctness under `replaceAll` when first match is not on line 1 — implementation is sound (uses `positions[0]`), but a test would harden the contract. Future work.
  - `TextEditTool.Run` body ~55 lines; could extract a per-edit `ApplyEdit` helper. Style-only.
- Base SHA: c3fad43d
- Head SHA: 283aaaac

## Resolution
- Issues found: 0 critical, 0 important (all reviewer items minor and beyond plan scope)
- Issues fixed: 0 (plan contract fully met; advisory items deferred)
- Final status: ✅ Approved
