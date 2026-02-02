# McpServerText Tool Interface Clarity Improvements

## Problem

Seven issues in the McpServerText MCP tool interfaces confuse agents:

1. **Stale MCP param descriptions** in TextPatch list removed targets (`text`, `pattern`, `section`, `afterHeading`) and omit `appendToSection`
2. **Stale `replaceLines` operation** still advertised in TextPatch MCP param description
3. **TextPatch vs TextReplace** overlap without clear differentiation
4. **Three search mechanisms** (TextSearch vault-wide, TextInspect search mode, TextRead search target) with unclear roles
5. **Heading target format differs** between TextRead (`{"heading": {"text": "Title"}}`) and TextPatch (`{"heading": "## Title"}`)
6. **Target availability unclear** across tools — agents guess which targets work where
7. **Media-library language** in file system tool descriptions irrelevant to text vault context

## Changes

### Issues 1 & 2: Fix stale TextPatch MCP parameter descriptions

**`McpServerText/McpTools/McpTextPatchTool.cs`:**

- `operation` param description: change to `"Operation: 'replace', 'insert', or 'delete'"` (remove `replaceLines`)
- `target` param description: change to `"Target specification as JSON. Use ONE of: lines {start,end}, heading, beforeHeading, appendToSection, codeBlock {index}"` (remove `text`, `pattern`, `section`, `afterHeading`; add `appendToSection`)

### Issue 3: Sharpen TextPatch and TextReplace descriptions

**`Domain/Tools/Text/TextPatchTool.cs`** — update `Description` constant:

```
Modifies a text or markdown file by structural position (heading, line range, code block).

Operations:
- 'replace': Replace targeted lines with new content
- 'insert': Insert new content at target location
- 'delete': Remove targeted content

Targeting (use ONE):
- lines: { "start": N, "end": M } - Target specific line range
- heading: "## Title" - Target a markdown heading line
- beforeHeading: "## Title" - Position before a heading (for insert)
- appendToSection: "## Title" - Append to end of a markdown section (for insert)
- codeBlock: { "index": N } - Target Nth code block content

IMPORTANT:
1. Always use TextInspect first to find exact line numbers
2. Prefer heading targeting for markdown—survives other edits
3. Line numbers shift after insert/delete—re-inspect if making multiple edits
4. For text find-and-replace by content match, use TextReplace instead

Examples:
- Replace heading: operation="replace", target={ "heading": "## Old" }, content="## New"
- Insert before heading: operation="insert", target={ "beforeHeading": "## Setup" }, content="## New Section\n"
- Append to section: operation="insert", target={ "appendToSection": "## Setup" }, content="New content\n"
- Delete lines 50-55: operation="delete", target={ "lines": { "start": 50, "end": 55 } }
- Replace code block: operation="replace", target={ "codeBlock": { "index": 0 } }, content="new code..."
```

**`Domain/Tools/Text/TextReplaceTool.cs`** — update `Description` constant to add cross-reference:

Add at the end: `"For structural edits by heading, line range, or code block position, use TextPatch instead."`

### Issue 4: Remove TextRead search target

**`Domain/Tools/Text/TextReadTool.cs`:**

- Remove the `search` case from `ResolveTarget`
- Remove the `ResolveSearchTarget` method
- Update final error message to: `"Invalid target. Use one of: lines, heading, codeBlock, anchor, section"`
- Update `Description` constant: remove `search` target from list and examples, add note: "To search within a file, use TextInspect with search mode."

**`McpServerText/McpTools/McpTextReadTool.cs`:**

- Update `target` param description to: `"Target specification as JSON. Use ONE of: lines {start,end}, heading, codeBlock {index}, anchor, section"`

### Issue 5: Unify heading target to flat string

**`Domain/Tools/Text/TextReadTool.cs`:**

Change the heading case in `ResolveTarget` from:

```csharp
if (target.TryGetPropertyValue("heading", out var headingNode) && headingNode is JsonObject headingObj)
{
    var text = headingObj["text"]?.GetValue<string>();
    var includeChildren = headingObj["includeChildren"]?.GetValue<bool>() ?? true;
    return ResolveHeadingTarget(lines, text, includeChildren);
}
```

To:

```csharp
if (target.TryGetPropertyValue("heading", out var headingNode))
{
    var heading = headingNode?.GetValue<string>() ?? throw new ArgumentException("heading value required");
    return ResolveHeadingTarget(lines, heading);
}
```

Update `ResolveHeadingTarget` to:
- Accept a single string parameter (no `includeChildren`)
- Always include children (the current default)
- Normalize by stripping `#` prefix, matching by text content (same approach as TextPatch's `FindHeadingTarget`)

Update `Description` constant:
- Change heading example from `target={ "heading": { "text": "Installation" } }` to `target={ "heading": "## Installation" }`
- Change targeting list entry to: `heading: "## Section Name" - Read markdown section (includes child headings)`

### Issue 6: Clarify target availability

Already addressed by issues 4 and 5 above — both tools now have accurate, explicit target lists in their descriptions. The remaining TextRead targets (`anchor`, `section`) are read-only concepts that don't apply to patching, and TextPatch targets (`beforeHeading`, `appendToSection`) are write-only concepts that don't apply to reading. The updated descriptions make this clear.

### Issue 7: Generic file system tool descriptions

**`Domain/Tools/Files/ListDirectoriesTool.cs`** — update `Description`:

```
Lists all directories as a tree of absolute paths. Returns only directories, not files.
Call once per conversation and reuse the result—directory structure rarely changes.
```

**`Domain/Tools/Files/ListFilesTool.cs`** — update `Description`:

```
Lists all files in the specified directory. Returns only files, not directories.
The path must be absolute and derived from the ListDirectories tool.
```

**`Domain/Tools/Files/MoveTool.cs`** — update `Description`:

```
Moves and/or renames a file or directory. Both arguments must be absolute paths.
Equivalent to 'mv -T {SourcePath} {DestinationPath}' bash command.
The destination path must not exist. Parent directories are created automatically.
```

**`Domain/Tools/Files/RemoveFileTool.cs`** — update `Description`:

```
Removes a file by moving it to a trash folder.
The path must be absolute and derived from the ListFiles tool response.
```

## Tests

- Update existing TextRead tests that use `{"heading": {"text": ...}}` format to use `{"heading": "## ..."}` flat string format
- Update any tests using the `search` target on TextRead to expect an error
- Add a test verifying TextRead rejects unknown/removed targets with a clear error message
- Verify all existing TextPatch tests still pass (no code changes to TextPatch logic)

## Files Changed

| File | Change |
|------|--------|
| `McpServerText/McpTools/McpTextPatchTool.cs` | Fix stale param descriptions |
| `McpServerText/McpTools/McpTextReadTool.cs` | Update target param description |
| `Domain/Tools/Text/TextPatchTool.cs` | Sharpen Description constant |
| `Domain/Tools/Text/TextReplaceTool.cs` | Add cross-reference to Description |
| `Domain/Tools/Text/TextReadTool.cs` | Remove search target, unify heading format, update Description |
| `Domain/Tools/Files/ListDirectoriesTool.cs` | Generic description |
| `Domain/Tools/Files/ListFilesTool.cs` | Generic description |
| `Domain/Tools/Files/MoveTool.cs` | Generic description |
| `Domain/Tools/Files/RemoveFileTool.cs` | Generic description |
| `Tests/Unit/**/TextReadTool*Tests.cs` | Update heading format, remove search target tests |
