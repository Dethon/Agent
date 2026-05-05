# Edit Tool: Array Signature

Date: 2026-05-05
Status: Approved (pending implementation plan)

## Summary

Replace the single-edit signature on the agent's text-edit tool with an array of edits applied sequentially against one file, atomically (all-or-nothing).

## Motivation

A single LLM tool call should be able to make several independent textual changes to the same file without round-tripping through the agent loop once per change. This matches the established `MultiEdit` pattern (e.g. Claude Code) and reduces token cost and latency.

## Scope

- **In scope:** Multiple `(oldString, newString, replaceAll)` operations against a single `filePath`.
- **Out of scope:** Cross-file batching. Backwards-compatible single-edit shim. Renaming the tool (stays `text_edit` / `fs_edit`).

## New DTO

`Domain/DTOs/TextEdit.cs`:

```csharp
public record TextEdit(string OldString, string NewString, bool ReplaceAll = false);
```

`[Description]` attributes are placed on the record properties so `AIFunctionFactory` produces a schema entry per field.

## Affected Components

Top-down through the call chain:

### 1. `Domain/Tools/FileSystem/VfsTextEditTool.cs`

`RunAsync` becomes:

```csharp
public async Task<JsonNode> RunAsync(
    [Description("Virtual path to file (e.g., /library/notes/todo.md)")]
    string filePath,
    [Description("Edits to apply in order, atomically. Must be non-empty.")]
    IReadOnlyList<TextEdit> edits,
    CancellationToken cancellationToken = default)
```

The tool description is updated to explain ordering and atomicity.

### 2. `Domain/Contracts/IFileSystemBackend.cs`

```csharp
Task<JsonNode> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct);
```

### 3. `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs`

Serializes the array under the MCP arg name `edits` as a list of dictionaries:

```csharp
["edits"] = edits.Select(e => new Dictionary<string, object?>
{
    ["oldString"] = e.OldString,
    ["newString"] = e.NewString,
    ["replaceAll"] = e.ReplaceAll
}).ToList()
```

### 4. `Domain/Tools/Text/TextEditTool.cs`

`Run` signature becomes `Run(string filePath, IReadOnlyList<TextEdit> edits)`. Implementation:

1. Reject empty `edits` (`ArgumentException`).
2. Read the file once into memory.
3. Iterate edits in order, applying each against the running in-memory string:
   - Find occurrences of `OldString` in the **current** content.
   - Apply existing single-edit rules: not-found → throw with case-insensitive suggestion; multiple matches with `ReplaceAll=false` → throw; otherwise replace first match or all matches.
   - Track per-edit `occurrencesReplaced` and `affectedLines` (computed against the content state at the moment the edit is applied).
4. If any edit throws, abort — no file write occurs (atomicity for free).
5. After all edits succeed, perform a single temp-file write + rename.

### 5. `McpServerVault/McpTools/FsEditTool.cs` and `McpServerSandbox/McpTools/FsEditTool.cs`

`McpRun` accepts `IReadOnlyList<TextEdit> edits` and forwards to `Run`.

## Result Shape

```json
{
  "status": "success",
  "filePath": "/abs/path/to/file",
  "totalOccurrencesReplaced": 3,
  "edits": [
    { "occurrencesReplaced": 1, "affectedLines": { "start": 5, "end": 5 } },
    { "occurrencesReplaced": 2, "affectedLines": { "start": 12, "end": 14 } }
  ]
}
```

## Validation Rules

- `edits` must be non-empty.
- Each edit obeys existing single-edit rules (case-sensitive match, ambiguity check honors `replaceAll`).
- Sequential semantics: edit N sees the content produced by edits 1…N-1. This is allowed and tested.

## Failure Mode

All-or-nothing. The first failing edit throws and propagates; the file is never written. Partial application is impossible.

## Tests

### Updated

- `Tests/Unit/Domain/Tools/FileSystem/VfsTextEditToolTests.cs` — verify array dispatch to backend (`EditAsync` called with the supplied list).
- `Tests/Unit/Domain/Text/TextEditToolTests.cs` — adjust existing single-edit cases to wrap their args in a one-element list.

### New

- Multiple edits applied in order produce the expected final content.
- Edit N can match text introduced by edit N-1's `newString`.
- Mid-sequence failure (e.g. second edit's `oldString` not found) leaves the file unchanged on disk.
- Empty `edits` array is rejected with `ArgumentException`.
- Per-edit `affectedLines` reflects positions in the running state, not the original.
- `totalOccurrencesReplaced` is the sum of per-edit counts.

### Unchanged

- `Tests/Integration/McpServerTests/McpVaultServerTests.cs` only asserts tool presence.

## Non-Goals

- No `text_multi_edit` rename — the tool stays `text_edit` / `fs_edit` with the new signature.
- No deprecation shim. Callers and tests are updated in the same change set.
- No cross-file edits in one call.
