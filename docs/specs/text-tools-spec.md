# Text/Markdown Tools Specification

> MCP tools for inspecting and patching text and markdown files

## Problem Statement

LLMs need to modify text files (documentation, configs, markdown), but:
1. Large documents exceed context limits—LLMs can't read entire files
2. Line numbers are unstable after edits (insertions shift subsequent lines)
3. LLMs need to target specific locations without seeing full content
4. Markdown has structural elements (headings, code blocks) that provide natural anchors
5. Raw regex operations are error-prone for LLMs

## Solution Overview

Three complementary MCP tools:

| Tool | Purpose |
|------|---------|
| **TextInspect** | Explore document structure and locate content without loading full file |
| **TextRead** | Read specific sections by line range, heading, or anchor |
| **TextPatch** | Modify documents using location-based operations |

---

## Tool 1: TextInspect

### Purpose
Returns the **structure** of a text/markdown document, allowing the LLM to understand organization without reading full content.

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filePath` | string | Yes | Absolute path to the text file |
| `mode` | string | No | One of: `structure`, `search`, `lines` (default: `structure`) |
| `query` | string | No | Search pattern for `search` mode (literal text or regex) |
| `regex` | boolean | No | Treat query as regex pattern (default: false) |
| `context` | int | No | Lines of context around search matches (default: 0) |

### Returns (Structure Mode)

For markdown files:
```json
{
  "filePath": "/docs/README.md",
  "totalLines": 847,
  "fileSize": "32KB",
  "format": "markdown",
  "structure": {
    "frontmatter": { "startLine": 1, "endLine": 5, "keys": ["title", "date", "tags"] },
    "headings": [
      { "level": 1, "text": "Project Overview", "line": 7 },
      { "level": 2, "text": "Installation", "line": 15, "children": [
        { "level": 3, "text": "Prerequisites", "line": 18 },
        { "level": 3, "text": "Quick Start", "line": 35 }
      ]},
      { "level": 2, "text": "Configuration", "line": 52 },
      { "level": 2, "text": "API Reference", "line": 120 }
    ],
    "codeBlocks": [
      { "language": "bash", "startLine": 20, "endLine": 25 },
      { "language": "json", "startLine": 55, "endLine": 72 },
      { "language": "typescript", "startLine": 125, "endLine": 180 }
    ],
    "anchors": [
      { "id": "installation", "line": 15 },
      { "id": "config-options", "line": 52 }
    ]
  }
}
```

For plain text files:
```json
{
  "filePath": "/config/settings.conf",
  "totalLines": 234,
  "fileSize": "8KB",
  "format": "text",
  "structure": {
    "sections": [
      { "marker": "[database]", "line": 5 },
      { "marker": "[cache]", "line": 45 },
      { "marker": "[logging]", "line": 89 }
    ],
    "blankLineGroups": [4, 44, 88, 150],
    "commentBlocks": [
      { "startLine": 1, "endLine": 3, "prefix": "#" }
    ]
  }
}
```

### Returns (Search Mode)

```json
{
  "filePath": "/docs/README.md",
  "query": "database connection",
  "matches": [
    {
      "line": 58,
      "column": 12,
      "text": "Configure the database connection string:",
      "context": {
        "before": ["", "## Database Setup"],
        "after": ["```json", "{\"host\": \"localhost\"}"]
      },
      "nearestHeading": { "level": 2, "text": "Configuration", "line": 52 }
    },
    {
      "line": 142,
      "column": 5,
      "text": "The database connection pool size can be adjusted...",
      "nearestHeading": { "level": 3, "text": "Performance Tuning", "line": 138 }
    }
  ],
  "totalMatches": 2
}
```

### Returns (Lines Mode)

Quick overview of specific line ranges:
```json
{
  "filePath": "/docs/README.md",
  "query": "50-60",
  "lines": [
    { "number": 50, "text": "" },
    { "number": 51, "text": "## Configuration" },
    { "number": 52, "text": "" },
    { "number": 53, "text": "The following options are available:" }
  ]
}
```

### Behavior

1. **Structure mode**: Parse document to extract headings, code blocks, sections
2. **Search mode**: Find text/pattern matches with context and structural location
3. **Lines mode**: Return specific line ranges (e.g., "1-10", "50-60,100-110")
4. **Never return full content** in structure mode—only metadata and locations

### Description for LLM

```
Inspects a text or markdown file to understand its structure without loading full content.

Modes:
- 'structure' (default): Returns document outline—headings, code blocks, sections
- 'search': Finds text or regex patterns, returns line numbers and context
- 'lines': Returns specific line ranges for quick preview

Use this before TextPatch to:
1. Understand document organization
2. Find the exact line numbers for content you want to modify
3. Locate headings or sections by name
4. Search for specific text to find where changes are needed

Examples:
- Get markdown outline: mode="structure"
- Find all mentions of "config": mode="search", query="config"
- Find regex pattern: mode="search", query="TODO:.*", regex=true
- Preview lines 50-60: mode="lines", query="50-60"
```

---

## Tool 2: TextRead

### Purpose
Reads specific portions of a text file by various targeting methods.

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filePath` | string | Yes | Absolute path to the text file |
| `target` | object | Yes | Specifies what to read (see Target Object) |

### Target Object Schema

Only ONE targeting method should be used:

```json
// By line range
{ "lines": { "start": 50, "end": 75 } }

// By heading (markdown only)
{ "heading": { "text": "Installation", "includeChildren": true } }

// By code block index (markdown only)  
{ "codeBlock": { "index": 2 } }

// By anchor/id (markdown only)
{ "anchor": "config-options" }

// By section marker (config files)
{ "section": "[database]" }

// By search match (read around first match)
{ "search": { "query": "DATABASE_URL", "contextLines": 10 } }
```

### Returns

```json
{
  "filePath": "/docs/README.md",
  "target": { "heading": { "text": "Installation" } },
  "range": { "startLine": 15, "endLine": 51 },
  "content": "## Installation\n\nFollow these steps to install...\n...",
  "truncated": false
}
```

If content is too large:
```json
{
  "filePath": "/docs/README.md",
  "target": { "heading": { "text": "API Reference" } },
  "range": { "startLine": 120, "endLine": 450 },
  "content": "## API Reference\n\n### Authentication\n...[first 200 lines]...",
  "truncated": true,
  "totalLines": 330,
  "suggestion": "Use TextInspect to find specific subsections, or target by line range"
}
```

### Behavior

1. **Line targeting**: Direct line numbers, most precise
2. **Heading targeting**: Read from heading to next same-or-higher level heading
3. **Code block targeting**: Read specific fenced code block by index (0-based)
4. **Anchor targeting**: Read from anchor to next heading
5. **Section targeting**: For INI-style files, read from marker to next marker
6. **Search targeting**: Read lines around first match

### Description for LLM

```
Reads a specific section of a text file. Use after TextInspect to read targeted content.

Targeting methods (use ONE):
- lines: { start: N, end: M } - Read specific line range
- heading: { text: "Section Name", includeChildren: true/false } - Read markdown section
- codeBlock: { index: N } - Read Nth code block (0-based)
- anchor: "anchor-id" - Read from anchor to next heading
- section: "[marker]" - Read INI-style section
- search: { query: "text", contextLines: N } - Read around first match

Best practices:
1. Always use TextInspect first to find line numbers or heading names
2. Prefer heading/section targeting for markdown—more stable than line numbers
3. Use line targeting when you need exact control
4. Large sections may be truncated—use narrower targets

Examples:
- Read lines 50-75: target={ "lines": { "start": 50, "end": 75 } }
- Read Installation section: target={ "heading": { "text": "Installation" } }
- Read third code block: target={ "codeBlock": { "index": 2 } }
```

---

## Tool 3: TextPatch

### Purpose
Modifies a text file using various patch operations with flexible targeting.

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filePath` | string | Yes | Absolute path to the text file |
| `operation` | string | Yes | One of: `replace`, `insert`, `delete`, `replaceLines` |
| `target` | object | Yes | Where to apply the operation (see Target Object) |
| `content` | string | No* | New content for replace/insert operations |
| `preserveIndent` | boolean | No | Match indentation of target line (default: true) |

\* Required for `replace`, `insert` operations

### Target Object Schema

```json
// By line range (most precise)
{ "lines": { "start": 50, "end": 52 } }

// By literal text match (replaces first occurrence)
{ "text": "old exact text to find" }

// By regex pattern (replaces first match)
{ "pattern": "TODO:\\s*(.+)", "flags": "i" }

// By heading (markdown) - targets the heading line itself
{ "heading": "## Installation" }

// After heading (markdown) - inserts after heading line
{ "afterHeading": "## Installation" }

// Before heading (markdown) - inserts before heading line  
{ "beforeHeading": "## New Section" }

// By code block (replaces entire block content)
{ "codeBlock": { "index": 2 } }

// By anchor
{ "anchor": "config-section" }

// By section marker (INI-style)
{ "section": "[database]", "key": "host" }
```

### Operations

| Operation | Behavior |
|-----------|----------|
| `replace` | Replace targeted content with new content |
| `insert` | Insert content at target location (before for lines, after for headings) |
| `delete` | Remove targeted content |
| `replaceLines` | Replace specific line range, handling line count changes |

### Returns

Success:
```json
{
  "status": "success",
  "operation": "replace",
  "filePath": "/docs/README.md",
  "target": { "heading": "## Installation" },
  "affectedLines": { "start": 15, "end": 15 },
  "linesChanged": 1,
  "linesDelta": 0,
  "preview": {
    "before": "## Installation",
    "after": "## Getting Started"
  }
}
```

For multi-line operations:
```json
{
  "status": "success",
  "operation": "replaceLines",
  "affectedLines": { "start": 50, "end": 55 },
  "linesChanged": 6,
  "linesDelta": -2,
  "note": "File now has 845 lines (was 847)"
}
```

Error:
```json
{
  "status": "error",
  "message": "Target not found: heading '## Instalation'",
  "suggestions": [
    "Similar heading found: '## Installation' at line 15",
    "Use TextInspect to see all headings in the document"
  ]
}
```

### Behavior

1. **Text/pattern matching**: Case-sensitive by default, matches first occurrence
2. **Line operations**: Use 1-based line numbers from TextInspect
3. **Heading operations**: Match exact heading text including `#` markers
4. **Code block replacement**: Replaces content inside fences, preserves language tag
5. **Indent preservation**: By default, matches indentation of first target line
6. **Atomic writes**: Write to temp file, then rename (prevents corruption)

### Description for LLM

```
Modifies a text or markdown file with precise targeting.

Operations:
- 'replace': Replace targeted text/lines with new content
- 'insert': Insert new content at target location
- 'delete': Remove targeted content
- 'replaceLines': Replace a range of lines (handles line count changes)

Targeting (use ONE):
- lines: { start: N, end: M } - Target specific line range
- text: "exact text" - Find and target literal text
- pattern: "regex" - Find and target regex match
- heading: "## Title" - Target a markdown heading line
- afterHeading: "## Title" - Position after a heading
- beforeHeading: "## Title" - Position before a heading
- codeBlock: { index: N } - Target Nth code block content
- section: "[name]" - Target INI section

IMPORTANT:
1. Always use TextInspect first to find exact line numbers and text
2. Prefer heading/section targeting for markdown—survives other edits
3. Use text/pattern targeting for inline changes
4. Line numbers shift after insert/delete—re-inspect if making multiple edits

Examples:
- Replace heading: operation="replace", target={ "heading": "## Old" }, content="## New"
- Insert after heading: operation="insert", target={ "afterHeading": "## Intro" }, content="\nNew paragraph..."
- Delete lines 50-55: operation="delete", target={ "lines": { "start": 50, "end": 55 } }
- Replace code block: operation="replace", target={ "codeBlock": { "index": 0 } }, content="new code..."
- Find and replace: operation="replace", target={ "text": "v1.0.0" }, content="v2.0.0"
```

---

## Workflow Examples

### Example 1: Add a New Section to README

```
1. LLM calls TextInspect
   filePath: "/docs/README.md"
   mode: "structure"
   
   → Returns: headings list showing "## Contributing" at line 200

2. LLM calls TextPatch
   filePath: "/docs/README.md"
   operation: "insert"
   target: { "beforeHeading": "## Contributing" }
   content: "## Troubleshooting\n\nCommon issues and solutions:\n\n### Build Errors\n\n..."
   
   → Returns: { status: "success", linesChanged: 0, linesDelta: 15 }
```

### Example 2: Update a Code Example

```
1. LLM calls TextInspect
   filePath: "/docs/API.md"
   mode: "structure"
   
   → Returns: codeBlocks showing typescript block at index 3, lines 125-145

2. LLM calls TextRead
   filePath: "/docs/API.md"
   target: { "codeBlock": { "index": 3 } }
   
   → Returns: current code content

3. LLM calls TextPatch
   filePath: "/docs/API.md"
   operation: "replace"
   target: { "codeBlock": { "index": 3 } }
   content: "const client = new ApiClient({\n  baseUrl: 'https://api.example.com',\n  timeout: 5000\n});"
```

### Example 3: Fix a Typo Found by Search

```
1. LLM calls TextInspect
   filePath: "/docs/guide.md"
   mode: "search"
   query: "recieve"
   
   → Returns: match at line 87, column 15

2. LLM calls TextPatch
   filePath: "/docs/guide.md"
   operation: "replace"
   target: { "text": "recieve" }
   content: "receive"
   
   → Returns: { status: "success", affectedLines: { start: 87, end: 87 } }
```

### Example 4: Update Config File Section

```
1. LLM calls TextInspect
   filePath: "/config/app.conf"
   mode: "structure"
   
   → Returns: sections showing "[database]" at line 45

2. LLM calls TextRead
   filePath: "/config/app.conf"
   target: { "section": "[database]" }
   
   → Returns: current database config

3. LLM calls TextPatch
   filePath: "/config/app.conf"
   operation: "replace"
   target: { "section": "[database]", "key": "host" }
   content: "host = production.db.example.com"
```

### Example 5: Batch Updates with Re-inspection

```
1. LLM calls TextInspect to get structure
2. LLM calls TextPatch to insert new section (adds 20 lines)
3. LLM calls TextInspect again (line numbers have shifted!)
4. LLM calls TextPatch for second edit using fresh line numbers

Alternative (more stable):
1. TextInspect once
2. TextPatch using heading-based targeting (doesn't depend on line numbers)
3. TextPatch using heading-based targeting for second edit
```

---

## Implementation Notes

### File Location
- `McpServerLibrary/McpTools/McpTextInspectTool.cs`
- `McpServerLibrary/McpTools/McpTextReadTool.cs`
- `McpServerLibrary/McpTools/McpTextPatchTool.cs`

### Domain Classes
- `Domain/Tools/Text/TextInspectTool.cs`
- `Domain/Tools/Text/TextReadTool.cs`
- `Domain/Tools/Text/TextPatchTool.cs`
- `Domain/Tools/Text/MarkdownParser.cs` (heading/code block extraction)
- `Domain/Tools/Text/TextTarget.cs` (target object parsing)

### Markdown Parsing

Use a lightweight approach—don't need full AST:

```csharp
public record MarkdownHeading(int Level, string Text, int Line);
public record MarkdownCodeBlock(string? Language, int StartLine, int EndLine);
public record MarkdownFrontmatter(int StartLine, int EndLine, IReadOnlyList<string> Keys);

public class MarkdownStructure
{
    public MarkdownFrontmatter? Frontmatter { get; init; }
    public IReadOnlyList<MarkdownHeading> Headings { get; init; }
    public IReadOnlyList<MarkdownCodeBlock> CodeBlocks { get; init; }
    public IReadOnlyList<string> Anchors { get; init; }
}
```

### Line Number Handling

1. All line numbers are **1-based** (matches editor conventions)
2. Line ranges are **inclusive** (start=5, end=10 includes lines 5,6,7,8,9,10)
3. After insertions/deletions, cached line numbers are invalid—require re-inspect

### Error Messages Should Include

- What was attempted
- Why it failed  
- Similar matches if target not found exactly
- Suggestion to use TextInspect if structure unclear

### Validation Rules

1. `filePath` must be within allowed paths
2. Line numbers must be within file bounds
3. Regex patterns must be valid
4. Heading targets must include `#` markers
5. Content for insert/replace must not be empty (use delete instead)

### Safety Features

1. **Atomic writes**: Write to `.tmp` file, verify, then rename
2. **Backup option**: Optionally create `.bak` before modification
3. **Line count validation**: Warn if deleting large sections
4. **Encoding preservation**: Detect and preserve file encoding (UTF-8, etc.)

---

## Comparison with JSON Tools

| Aspect | JSON Tools | Text Tools |
|--------|------------|------------|
| Structure | Hierarchical (paths) | Linear (lines) + semantic (headings) |
| Targeting | JSON Pointer, match-by-property | Lines, text, pattern, heading, section |
| Inspection | Returns schema/shape | Returns outline/locations |
| Stability | Paths stable across edits | Line numbers shift; prefer semantic targets |
| Validation | JSON schema | Regex, heading existence |

---

## Future Enhancements

1. **Diff preview**: Return unified diff before applying patch
2. **Multi-file operations**: Apply same pattern across multiple files
3. **Transaction mode**: Queue multiple patches, apply atomically
4. **Undo support**: Track recent changes for rollback
5. **Template insertion**: Insert content with placeholder substitution
6. **Markdown link validation**: Check for broken internal links after edit
