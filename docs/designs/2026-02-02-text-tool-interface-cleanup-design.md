# Text Tool Interface Cleanup Design

## Problem

The McpServerText toolset has 6 interface issues that confuse agents:

1. **TextPatch vs TextReplace overlap** — both edit files, unclear when to use which
2. **TextInspect search mode vs TextSearch** — duplicate single-file search capability
3. **TextInspect query param overloading** — same param means search pattern or line range depending on mode
4. **Target key mismatch** — TextRead and TextPatch accept different target keys under the same `target` param name
5. **path/filePath inconsistency** — `path` means relative dir, absolute dir, or absolute file depending on tool
6. **TextInspect lines mode vs TextRead lines target** — two tools for reading line ranges

## Solution

### Tool Inventory Changes

**Before (10 tools):** TextRead, TextPatch, TextReplace, TextInspect, TextSearch, TextCreate, ListDirectories, ListFiles, Move, RemoveFile

**After (9 tools):** TextRead, **TextEdit**, TextInspect, TextSearch, TextCreate, ListDirectories, ListFiles, Move, RemoveFile

### 1. Merge TextPatch + TextReplace → TextEdit

Create a single `TextEdit` tool that handles all file modifications via positional targeting OR content matching.

**Name:** `TextEdit`

**Parameters:**

| Param | Type | Required | Description |
|-------|------|----------|-------------|
| `filePath` | string | yes | Path to file (absolute or relative to vault) |
| `operation` | string | yes | `"replace"`, `"insert"`, or `"delete"` |
| `target` | string (JSON) | yes | Targeting method (see below) |
| `content` | string | no | New content for replace/insert |
| `occurrence` | string | no | For `text` target only: `"first"` (default), `"last"`, `"all"`, or numeric 1-based index |
| `preserveIndent` | bool | no | Match indentation of target line (default: true) |
| `expectedHash` | string | no | 16-char file hash for staleness detection |

**Target keys (use ONE):**

| Key | Format | Purpose |
|-----|--------|---------|
| `lines` | `{"start": N, "end": M}` | Target line range |
| `heading` | `"## Title"` | Target heading line |
| `beforeHeading` | `"## Title"` | Position before heading (for insert) |
| `appendToSection` | `"## Title"` | Append to section end (for insert) |
| `codeBlock` | `{"index": N}` | Target Nth code block (0-based) |
| `text` | `"exact match"` | Target by content match (case-sensitive) |

**Implementation:** New `TextEditTool` in Domain that composes logic from `TextPatchTool` and `TextReplaceTool`. When target contains `text`, delegate to replace-by-content logic. Otherwise delegate to positional patch logic. Remove `TextPatchTool`, `TextReplaceTool`, `McpTextPatchTool`, `McpTextReplaceTool`.

### 2. Strip TextInspect to Structure-Only

Remove `mode`, `query`, `regex`, and `context` parameters. TextInspect always returns document structure.

**Parameters (after):**

| Param | Type | Required | Description |
|-------|------|----------|-------------|
| `filePath` | string | yes | Path to file (absolute or relative to vault) |

**Returns:** headings (level, text, line), code blocks (startLine, endLine, language), anchors, sections, frontmatter, totalLines, fileSize, fileHash.

This resolves issues 3 (query overloading) and 6 (lines mode overlap with TextRead).

### 3. Add Single-File Search to TextSearch

Add optional `filePath` parameter to TextSearch, replacing TextInspect's old search mode.

**New/changed parameters:**

| Param | Type | Change | Description |
|-------|------|--------|-------------|
| `filePath` | string | **new** | Optional. Search within this file only |
| `directoryPath` | string | **renamed from `path`** | Directory to search (default: `"/"`) |

When `filePath` is set, `directoryPath` and `filePattern` are ignored.

### 4. Standardize Parameter Names (Issue 5)

| Tool | Old Param | New Param |
|------|-----------|-----------|
| TextSearch | `path` | `directoryPath` |
| ListFiles | `path` | `directoryPath` |
| RemoveFile | `path` | `filePath` |
| Move | `sourcePath` / `destinationPath` | No change (already unambiguous) |

### 5. Improve Descriptions for Target Key Clarity (Issue 4)

TextRead and TextEdit have legitimately different target keys. Each tool's `target` parameter description lists only its valid keys — no cross-referencing needed since the lists are self-contained.

**TextRead target keys:** lines, heading, codeBlock, anchor, section

**TextEdit target keys:** lines, heading, beforeHeading, appendToSection, codeBlock, text

**Cross-references in tool-level descriptions:**
- TextEdit: "Use TextInspect first to find line numbers and headings. To search within files, use TextSearch."
- TextRead: "Use TextInspect first to find headings and structure."
- TextSearch: "To modify matching content, use TextEdit with a text target."

## Files Affected

### Domain Layer
- `Domain/Tools/Text/TextPatchTool.cs` — Remove
- `Domain/Tools/Text/TextReplaceTool.cs` — Remove
- **`Domain/Tools/Text/TextEditTool.cs`** — Create (merges patch + replace logic)
- `Domain/Tools/Text/TextInspectTool.cs` — Strip to structure-only, remove mode/query/regex/context
- `Domain/Tools/Text/TextSearchTool.cs` — Add filePath param, rename path → directoryPath
- `Domain/Tools/Text/TextReadTool.cs` — Update description
- `Domain/Tools/Files/ListFilesTool.cs` — Rename path → directoryPath
- `Domain/Tools/Files/RemoveFileTool.cs` — Rename path → filePath

### McpServerText Layer
- `McpServerText/McpTools/McpTextPatchTool.cs` — Remove
- `McpServerText/McpTools/McpTextReplaceTool.cs` — Remove
- **`McpServerText/McpTools/McpTextEditTool.cs`** — Create
- `McpServerText/McpTools/McpTextInspectTool.cs` — Simplify to single param
- `McpServerText/McpTools/McpTextSearchTool.cs` — Add filePath, rename path
- `McpServerText/McpTools/McpTextReadTool.cs` — Update description
- `McpServerText/McpTools/McpTextListFilesTool.cs` — Rename path
- `McpServerText/McpTools/McpRemoveFileTool.cs` — Rename path
- `McpServerText/Modules/ConfigModule.cs` — Update tool registrations

### Tests
- Remove tests for TextPatchTool, TextReplaceTool
- Create tests for TextEditTool (covering both positional and text-match targeting)
- Update TextInspectTool tests (structure-only)
- Update TextSearchTool tests (filePath param)
- Update parameter name references in all affected test files
