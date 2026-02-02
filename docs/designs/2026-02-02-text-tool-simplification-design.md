# Text Tool Simplification Design

Simplify McpServerText tools to match Claude Code's agentic patterns: flat scalar parameters, whole-file reads, exact string matching for edits.

## Tool Inventory

| Before | After | Change |
|--------|-------|--------|
| TextRead (target required) | **TextRead** (whole file, optional pagination) | Rewritten |
| TextEdit (6 targeting modes) | **TextEdit** (oldString → newString) | Rewritten |
| TextInspect | *deleted* | Removed |
| TextCreate (create-only) | **TextCreate** (+ overwrite) | Extended |
| TextSearch (7 params) | **TextSearch** (+ outputMode) | Extended |

5 tools → 4 tools. No JSON string parameters. No mandatory prerequisite calls.

## TextRead

```
TextRead(filePath, offset?, limit?)
```

| Param | Type | Default | Description |
|-------|------|---------|-------------|
| filePath | string | required | Path to file (absolute or relative to vault) |
| offset | int? | null | Start from this line number (1-based) |
| limit | int? | null | Max lines to return |

- Returns content with line numbers: `1: first line\n2: second line\n...`
- Max 500 lines returned; truncated with message suggesting offset/limit
- Trailing metadata line with totalLines and fileHash
- No targeting (heading, codeBlock, anchor, section all removed)

## TextEdit

```
TextEdit(filePath, oldString, newString, replaceAll?)
```

| Param | Type | Default | Description |
|-------|------|---------|-------------|
| filePath | string | required | Path to file (absolute or relative to vault) |
| oldString | string | required | Exact text to find (case-sensitive) |
| newString | string | required | Replacement text |
| replaceAll | bool | false | Replace all occurrences |

- When `replaceAll=false`: oldString must appear exactly once. Fails with occurrence count if ambiguous — agent must provide more context to disambiguate
- When `replaceAll=true`: replaces every occurrence
- Case-insensitive suggestion on not-found (existing behavior)
- Atomic write via temp file + move
- Insert = include surrounding context in oldString, add new lines in newString
- Delete = include content in oldString, omit it from newString
- Returns affected line range and fileHash

## TextCreate

```
TextCreate(filePath, content, overwrite?, createDirectories?)
```

| Param | Type | Default | Description |
|-------|------|---------|-------------|
| filePath | string | required | Path for the file |
| content | string | required | File content |
| overwrite | bool | false | Overwrite existing file |
| createDirectories | bool | true | Create parent directories |

- Only change: new `overwrite` parameter
- When false (default), fails if file exists (current behavior)
- When true, overwrites existing file

## TextSearch

```
TextSearch(query, regex?, filePath?, filePattern?, directoryPath?, maxResults?, contextLines?, outputMode?)
```

| Param | Type | Default | Description |
|-------|------|---------|-------------|
| outputMode | string | "content" | "content" = matching lines with context, "files_only" = file paths with match counts |

- Only change: new `outputMode` parameter
- When "files_only": returns `{file, matchCount}` per file, no match text or context
- All other parameters unchanged

## Deletions

### Files to delete

- `Domain/Tools/Text/TextInspectTool.cs`
- `Domain/Tools/Text/MarkdownParser.cs`
- `McpServerText/McpTools/McpTextInspectTool.cs`
- `Tests/Unit/Domain/Text/TextInspectToolTests.cs`
- `Tests/Unit/Domain/Text/MarkdownParserTests.cs`

### Code to remove

- TextInspect registration in McpServerText ConfigModule
- All MarkdownParser imports/usages
- TextReadTool: heading/codeBlock/anchor/section targeting, MarkdownParser dependency
- TextEditTool: positional targeting (lines, heading, beforeHeading, appendToSection, codeBlock), operation enum, target JSON parsing, preserveIndent, expectedHash, occurrence

## Files to Rewrite

| File | From | To |
|------|------|----|
| `Domain/Tools/Text/TextEditTool.cs` | 562 lines, 6 targeting modes | ~60 lines, oldString/newString |
| `Domain/Tools/Text/TextReadTool.cs` | 223 lines, 5 targeting modes | ~50 lines, whole-file + offset/limit |
| `McpServerText/McpTools/McpTextEditTool.cs` | JSON target param | flat scalar params |
| `McpServerText/McpTools/McpTextReadTool.cs` | JSON target param | flat scalar params |
| `Tests/Unit/Domain/Text/TextEditToolTests.cs` | positional + text tests | oldString/newString tests |
| `Tests/Unit/Domain/Text/TextReadToolTests.cs` | targeting tests | whole-file + pagination tests |

## Files to Modify

| File | Change |
|------|--------|
| `Domain/Tools/Text/TextCreateTool.cs` | Add `overwrite` parameter |
| `Domain/Tools/Text/TextSearchTool.cs` | Add `outputMode` parameter |
| `McpServerText/McpTools/McpTextCreateTool.cs` | Expose `overwrite` |
| `McpServerText/McpTools/McpTextSearchTool.cs` | Expose `outputMode` |
| `Tests/Unit/Domain/Text/TextCreateToolTests.cs` | Add overwrite test |
| `Tests/Unit/Domain/Text/TextSearchToolTests.cs` | Add outputMode test |

## What TextEdit Keeps from Current Code

- Vault path validation and extension whitelisting (from TextToolBase)
- Case-insensitive suggestion on not-found
- Atomic writes (temp file + move)
- File hash computation

## What TextRead Keeps

- Vault path validation and extension whitelisting
- File hash in response
- Truncation with helpful message
