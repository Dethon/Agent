# Text File Edit Improvements Design

## Problem

The McpServerText tools are causing inaccurate edits when the agent modifies markdown files. Text gets inserted in wrong locations due to:

1. **Stale line numbers**: After edit A shifts lines, the agent uses line numbers from a prior `TextInspect` call for edit B, targeting the wrong location.
2. **`afterHeading` resolves incorrectly**: It inserts immediately after the heading line rather than at the end of the section's content, wedging new text between a heading and its existing content.
3. **No simple search-and-replace**: The agent is forced into structural targeting (line numbers, JSON target objects) for simple inline text changes, increasing error surface.

## Solution Overview

Four changes, ordered by impact:

| Change | Impact | Description |
|--------|--------|-------------|
| New `TextReplace` tool | High | Simple old_text/new_text search-and-replace, no line numbers needed |
| New `appendToSection` target | High | Insert at end of a heading's section, not right after the heading line |
| Context window in patch responses | Medium | Return surrounding lines after edits so the agent has updated context |
| File hash staleness detection | Medium | Detect when a file changed between inspect and edit |

Additionally, remove confusing/redundant functionality from `TextPatch`.

## Detailed Design

### 1. New Tool: TextReplace

A new Domain tool `TextReplaceTool` and corresponding MCP wrapper `McpTextReplaceTool`.

**Interface:**

```csharp
// Domain/Tools/Text/TextReplaceTool.cs
public class TextReplaceTool(string vaultPath, string[] allowedExtensions)
{
    protected const string Name = "TextReplace";
    protected const string Description = """
        Replaces exact text in a file. This is the preferred tool for inline edits.

        Finds oldText in the file and replaces it with newText. The oldText can span
        multiple lines (use literal newlines). If oldText appears multiple times,
        use the occurrence parameter to specify which one.

        Always use TextInspect or TextRead first to get the exact text to replace.

        Examples:
        - Fix a typo: oldText="teh quick", newText="the quick"
        - Replace a paragraph: oldText="Old paragraph\ntext here", newText="New paragraph\ntext here"
        - Replace all occurrences: oldText="v1.0", newText="v2.0", occurrence="all"
        - Replace 2nd occurrence: oldText="TODO", newText="DONE", occurrence="2"
        """;

    protected JsonNode Run(string filePath, string oldText, string newText,
        string occurrence = "first", string? expectedHash = null);
}
```

**MCP wrapper:**

```csharp
// McpServerText/McpTools/McpTextReplaceTool.cs
[McpServerToolType]
public class McpTextReplaceTool(McpSettings settings)
    : TextReplaceTool(settings.VaultPath, settings.AllowedExtensions)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public CallToolResult McpRun(
        [Description("Path to the text file (absolute or relative to vault)")]
        string filePath,
        [Description("Exact text to find (can span multiple lines)")]
        string oldText,
        [Description("Text to replace it with")]
        string newText,
        [Description("Which occurrence: 'first' (default), 'last', 'all', or a number like '2' for 2nd")]
        string occurrence = "first",
        [Description("Expected file hash from a previous inspect/edit response (optional, prevents stale edits)")]
        string? expectedHash = null)
    {
        return ToolResponse.Create(Run(filePath, oldText, newText, occurrence, expectedHash));
    }
}
```

**Behavior:**

1. Read file content as a single string (preserving newlines).
2. Find all occurrences of `oldText` in the full content.
3. If no matches found: throw with helpful message (suggest case-insensitive match if found).
4. Based on `occurrence`:
   - `"first"`: Replace first occurrence. If multiple exist, include note with count and line numbers of other matches.
   - `"last"`: Replace last occurrence.
   - `"all"`: Replace all occurrences. Return count.
   - Numeric (e.g., `"2"`): Replace the Nth occurrence (1-based). Error if N > total matches.
5. Write file atomically (same temp-file pattern as TextPatch).
6. Return: affected line range, before/after preview, new file hash, total matches found.

**Response format:**

```json
{
  "status": "success",
  "filePath": "/path/to/file.md",
  "occurrencesFound": 3,
  "occurrencesReplaced": 1,
  "affectedLines": { "start": 15, "end": 17 },
  "preview": {
    "before": "old text here...",
    "after": "new text here..."
  },
  "context": {
    "beforeLines": [
      { "number": 13, "text": "line 13 content" },
      { "number": 14, "text": "line 14 content" }
    ],
    "afterLines": [
      { "number": 18, "text": "line 18 content" },
      { "number": 19, "text": "line 19 content" }
    ]
  },
  "fileHash": "abc123...",
  "note": "2 other occurrences found at lines 30, 45"
}
```

### 2. TextPatch Changes

#### 2a. New target: `appendToSection`

Resolves to the last content line of a heading's section (before the next same-or-higher level heading, or end of file).

```csharp
// In ResolveTarget method
if (target.TryGetPropertyValue("appendToSection", out var appendNode))
{
    if (!isMarkdown)
        throw new InvalidOperationException("Section targeting only works with markdown files");

    var heading = appendNode?.GetValue<string>()
        ?? throw new ArgumentException("appendToSection value required");

    var structure = MarkdownParser.Parse(lines.ToArray());
    var headingIndex = FindHeadingIndex(structure, heading);
    var endLine = MarkdownParser.FindHeadingEnd(structure.Headings, headingIndex, lines.Count);

    // endLine is the last line of the section content
    return (endLine, endLine, null);
}
```

When used with `operation: "insert"`, this appends content at the bottom of the section.

#### 2b. Remove `afterHeading`

Remove the `afterHeading` target entirely. It's replaced by `appendToSection` which does what agents actually intend.

#### 2c. Remove `replaceLines` operation

The `replace` operation with `lines` target already covers this. Remove to reduce confusion.

#### 2d. Remove `text` target

Replaced by the `TextReplace` tool which handles inline text edits better.

#### 2e. Remove `pattern` target

Regex targeting for edits is error-prone. Keep regex only in `TextInspect` (read-only search).

#### 2f. Remove `section` (INI-style) target

Rarely used, adds noise to tool description.

#### 2g. Add context window to responses

After every patch operation, include 3 lines before and after the affected range:

```csharp
// After writing the file, add context
var updatedLines = File.ReadAllLines(fullPath);
var contextBefore = new JsonArray();
var contextAfter = new JsonArray();

for (var i = Math.Max(0, startLine - 4); i < startLine - 1; i++)
    contextBefore.Add(new JsonObject { ["number"] = i + 1, ["text"] = updatedLines[i] });

var newEndLine = startLine + (updatedLines.Length - originalLineCount) + (endLine - startLine);
for (var i = newEndLine; i < Math.Min(updatedLines.Length, newEndLine + 3); i++)
    contextAfter.Add(new JsonObject { ["number"] = i + 1, ["text"] = updatedLines[i] });

result["context"] = new JsonObject
{
    ["beforeLines"] = contextBefore,
    ["afterLines"] = contextAfter
};
```

#### 2h. Add `expectedHash` parameter

Optional parameter. If provided, compare against current file hash before applying edit. If mismatch, return error with the current hash so the agent can re-inspect.

#### Final TextPatch interface:

```
TextPatch(filePath, operation, target, content?, preserveIndent?, expectedHash?)
```

**Operations**: `replace`, `insert`, `delete`

**Targets**:
- `lines: {start, end}` — line range
- `heading: "## Title"` — target heading line
- `appendToSection: "## Title"` — end of section (for insert)
- `beforeHeading: "## Title"` — before heading (for insert)
- `codeBlock: {index}` — code block content

**Updated description:**

```
Modifies a text or markdown file with structural targeting.

Operations:
- 'replace': Replace targeted content with new content
- 'insert': Insert new content at target location
- 'delete': Remove targeted content

Targeting (use ONE):
- lines: { "start": N, "end": M } - Target specific line range
- heading: "## Title" - Target a markdown heading line
- appendToSection: "## Title" - Position at end of a section (for insert)
- beforeHeading: "## Title" - Position before a heading (for insert)
- codeBlock: { "index": N } - Target Nth code block content

For simple text replacement (find & replace), use TextReplace instead.
Use TextInspect first to find exact targets.
Pass expectedHash from a previous response to detect stale edits.
```

### 3. TextInspect Changes

Add file hash to structure mode response:

```csharp
// In InspectStructure
var hash = Convert.ToHexString(
    System.Security.Cryptography.SHA256.HashBytes(
        System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines))));

result["fileHash"] = hash[..16]; // Short hash is sufficient
```

### 4. System Prompt Updates

Update `KnowledgeBasePrompt.AgentSystemPrompt` to guide the agent:

```
### Editing Best Practices

**For inline text changes (fix typos, update values, rewrite sentences):**
→ Use TextReplace. It finds exact text and replaces it. No line numbers needed.

**For structural changes (add new sections, delete blocks, insert under headings):**
→ Use TextPatch with heading-based targeting.

**For appending content to an existing section:**
→ Use TextPatch with appendToSection target (inserts at end of section).

**For multi-edit workflows:**
→ Pass expectedHash from each response to the next edit to detect conflicts.
→ After edits, use the context lines in the response to orient yourself.
→ Do NOT reuse line numbers from a previous TextInspect after making edits.

**Tool priority for edits:**
1. TextReplace — default choice for most edits
2. TextPatch with appendToSection/beforeHeading — for inserting new content
3. TextPatch with heading/codeBlock — for replacing entire sections
4. TextPatch with lines — last resort, line numbers are fragile
```

## Files to Create/Modify

| File | Action |
|------|--------|
| `Domain/Tools/Text/TextReplaceTool.cs` | **Create** — New domain tool |
| `McpServerText/McpTools/McpTextReplaceTool.cs` | **Create** — MCP wrapper |
| `Domain/Tools/Text/TextPatchTool.cs` | **Modify** — Add `appendToSection`, remove targets/ops, add hash + context |
| `McpServerText/McpTools/McpTextPatchTool.cs` | **Modify** — Add `expectedHash` parameter |
| `Domain/Tools/Text/TextInspectTool.cs` | **Modify** — Add file hash to responses |
| `Domain/Prompts/KnowledgeBasePrompt.cs` | **Modify** — Update system prompt with new guidance |
| `Tests/Unit/Tools/Text/TextReplaceToolTests.cs` | **Create** — Tests for new tool |
| `Tests/Unit/Tools/Text/TextPatchToolTests.cs` | **Modify** — Update tests for changed behavior |

## File Hash Implementation

Use a short SHA256 hash (first 16 hex chars) of the file content. Computed as:

```csharp
private static string ComputeFileHash(string[] lines)
{
    var content = string.Join("\n", lines);
    var bytes = System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(content));
    return Convert.ToHexString(bytes)[..16];
}
```

The hash is:
- Returned in every TextInspect (structure mode), TextPatch, and TextReplace response
- Accepted optionally as `expectedHash` in TextPatch and TextReplace
- If `expectedHash` is provided and doesn't match: throw with current hash in the error message

## Migration Notes

- `afterHeading` removal is a breaking change for any existing agent prompts that reference it
- `replaceLines` removal requires checking if any prompts reference this operation
- The system prompt update should be deployed simultaneously with the tool changes
