# Plan: MCP Text Tool Interface Clarity Improvements

## Summary

Update MCP text tool descriptions, parameter annotations, and the TextReadTool heading format to eliminate agent confusion. Seven issues are addressed: stale TextPatch param descriptions, stale `replaceLines` mention, unclear TextPatch vs TextReplace differentiation, overlapping search mechanisms, inconsistent heading target format between TextRead and TextPatch, unclear target availability, and media-library language in file system tool descriptions.

## Files

> **Note**: This is the canonical file list.

### Files to Edit
- `McpServerText/McpTools/McpTextPatchTool.cs`
- `McpServerText/McpTools/McpTextReadTool.cs`
- `Domain/Tools/Text/TextPatchTool.cs`
- `Domain/Tools/Text/TextReplaceTool.cs`
- `Domain/Tools/Text/TextReadTool.cs`
- `Domain/Tools/Files/ListDirectoriesTool.cs`
- `Domain/Tools/Files/ListFilesTool.cs`
- `Domain/Tools/Files/MoveTool.cs`
- `Domain/Tools/Files/RemoveFileTool.cs`
- `Tests/Unit/Domain/Text/TextReadToolTests.cs`

### Files to Create
(none)

## Code Context

**TextPatchTool** (`Domain/Tools/Text/TextPatchTool.cs`):
- `Description` constant at lines 9-36: Already has the correct description from a previous PR. The design doc proposes the same text. Comparing line-by-line, the current description at line 28 says `"4. For text find-and-replace, use TextReplace instead"` while the design says `"4. For text find-and-replace by content match, use TextReplace instead"`. This is a minor wording improvement to apply.
- `ResolveTarget` at line 147: Handles `lines`, `heading`, `appendToSection`, `beforeHeading`, `codeBlock` plus deprecated target errors. No logic changes needed.

**McpTextPatchTool** (`McpServerText/McpTools/McpTextPatchTool.cs`):
- Line 20: `operation` param description says `"Operation: 'replace', 'insert', 'delete', or 'replaceLines'"` -- must remove `'replaceLines'`.
- Lines 22-23: `target` param description lists deprecated targets (`text`, `pattern`, `section`, `afterHeading`) and omits `appendToSection` -- must update.

**TextReadTool** (`Domain/Tools/Text/TextReadTool.cs`):
- `Description` constant at lines 9-30: Lists `search` target and uses `{"heading": {"text": ...}}` format. Must update.
- `ResolveTarget` at line 69: Contains `search` case at lines 120-125 and heading case at lines 78-88 using `JsonObject` format. Must change heading to flat string and remove search.
- `ResolveHeadingTarget` at line 130: Takes `(string[] lines, string text, bool includeChildren)`. Must simplify to single string, always include children.
- `ResolveSearchTarget` at line 221: Must be removed entirely.

**McpTextReadTool** (`McpServerText/McpTools/McpTextReadTool.cs`):
- Lines 20-21: `target` param description lists `search` and old heading format. Must update.

**TextReplaceTool** (`Domain/Tools/Text/TextReplaceTool.cs`):
- `Description` constant at lines 9-39: Must add cross-reference to TextPatch at the end.

**File system tools** (`Domain/Tools/Files/`):
- `ListDirectoriesTool.cs` lines 12-20: Contains "library" language. Must genericize.
- `ListFilesTool.cs` lines 12-18: Contains "library" language. Must genericize.
- `MoveTool.cs` lines 11-19: Contains "library" language. Must genericize.
- `RemoveFileTool.cs` lines 11-15: Contains "library" language. Must genericize.

**TextReadToolTests** (`Tests/Unit/Domain/Text/TextReadToolTests.cs`):
- Line 56-57: Uses `{"heading": {"text": "Setup", "includeChildren": false}}` -- must change to flat string.
- Line 155-156: Uses `{"heading": {"text": "Instalation"}}` -- must change to flat string.
- Lines 109-129: `Run_SearchTarget_ReturnsContextAroundMatch` test -- must change to expect error.

**TextPatchToolTests** (`Tests/Unit/Domain/Text/TextPatchToolTests.cs`):
- No changes needed. All existing tests use the correct formats already.

## External Context

N/A -- all changes are to tool descriptions and internal logic. No external libraries or APIs involved.

## Architectural Narrative

### Task

Update MCP text tool interfaces to eliminate seven sources of agent confusion: stale parameter descriptions, deprecated operation references, unclear tool differentiation, overlapping search mechanisms, inconsistent heading target format, unclear target availability, and media-library-specific language in generic file system tools.

### Architecture

The MCP tools follow a two-layer pattern:
- **Domain tools** (`Domain/Tools/Text/*.cs`, `Domain/Tools/Files/*.cs`): Contain `Name` and `Description` constants plus protected `Run` methods with business logic.
- **MCP wrapper tools** (`McpServerText/McpTools/*.cs`): Inherit from Domain tools, add `[McpServerTool]` / `[Description]` attributes on a public `McpRun` method, and pass through to the base `Run`.

Agent-facing descriptions live in two places:
1. The `Description` constant on the Domain tool class (used by `[Description(Description)]` on the MCP method)
2. `[Description("...")]` attributes on individual MCP method parameters

### Selected Context

- `Domain/Tools/Text/TextPatchTool.cs:9-36` -- Description constant (mostly correct, minor wording tweak)
- `Domain/Tools/Text/TextPatchTool.cs:147-230` -- ResolveTarget (no logic changes)
- `Domain/Tools/Text/TextReadTool.cs:9-30` -- Description constant (needs update)
- `Domain/Tools/Text/TextReadTool.cs:69-128` -- ResolveTarget (remove search, change heading)
- `Domain/Tools/Text/TextReadTool.cs:130-168` -- ResolveHeadingTarget (simplify signature)
- `Domain/Tools/Text/TextReadTool.cs:221-235` -- ResolveSearchTarget (delete entirely)
- `Domain/Tools/Text/TextReplaceTool.cs:9-39` -- Description constant (add cross-reference)
- `McpServerText/McpTools/McpTextPatchTool.cs:20-23` -- Param descriptions (fix stale)
- `McpServerText/McpTools/McpTextReadTool.cs:20-21` -- Param description (fix)
- `Domain/Tools/Files/ListDirectoriesTool.cs:12-20` -- Description (genericize)
- `Domain/Tools/Files/ListFilesTool.cs:12-18` -- Description (genericize)
- `Domain/Tools/Files/MoveTool.cs:11-19` -- Description (genericize)
- `Domain/Tools/Files/RemoveFileTool.cs:11-15` -- Description (genericize)
- `Tests/Unit/Domain/Text/TextReadToolTests.cs` -- Update heading format tests, convert search test to error test

### Relationships

- `McpTextPatchTool` inherits `TextPatchTool` -- param descriptions on MCP layer, tool description on Domain layer
- `McpTextReadTool` inherits `TextReadTool` -- param descriptions on MCP layer, tool description on Domain layer
- `TextReadToolTests` directly tests `TextReadTool.Run` via `TestableTextReadTool` wrapper
- File system tools (`ListDirectoriesTool`, `ListFilesTool`, `MoveTool`, `RemoveFileTool`) are standalone Domain tools with no inter-dependencies in this plan

### External Context

No external libraries or frameworks involved. All changes are to string constants and internal method signatures.

### Implementation Notes

1. The `TextPatchTool.Description` constant is already very close to the design target. The only difference is line 28: current says `"4. For text find-and-replace, use TextReplace instead"` and design says `"4. For text find-and-replace by content match, use TextReplace instead"`.
2. The `TextReadTool.ResolveHeadingTarget` currently accepts `(string[] lines, string text, bool includeChildren)` and has two code paths based on `includeChildren`. The new version always includes children (the default behavior), matching how TextPatch resolves headings. We use `MarkdownParser.FindHeadingEnd` for end-line calculation.
3. The heading input format change from `{"heading": {"text": "Title"}}` to `{"heading": "## Title"}` means the `ResolveTarget` heading case must read a string value instead of a `JsonObject`. The matching logic should normalize by stripping `#` prefix, like TextPatch's `FindHeadingTarget`.
4. Removing `search` target from TextReadTool means the `ResolveSearchTarget` private method is deleted entirely and the `search` case in `ResolveTarget` is removed.

### Ambiguities

None. The design document is fully specified with exact text for all description changes and precise code transformations.

### Requirements

1. McpTextPatchTool `operation` param description must say `"Operation: 'replace', 'insert', or 'delete'"` (no `replaceLines`)
2. McpTextPatchTool `target` param description must list: `lines {start,end}`, `heading`, `beforeHeading`, `appendToSection`, `codeBlock {index}` (no `text`, `pattern`, `section`, `afterHeading`)
3. TextPatchTool `Description` constant must say `"by content match"` in the TextReplace cross-reference
4. TextReplaceTool `Description` must end with `"For structural edits by heading, line range, or code block position, use TextPatch instead."`
5. TextReadTool `Description` must not mention `search` target; must use `heading: "## Section Name"` flat string format
6. TextReadTool heading resolution must accept flat string `"## Title"` (not `{"text": "Title"}`)
7. TextReadTool heading resolution must always include children (no `includeChildren` parameter)
8. TextReadTool `search` target must be removed (method and case deleted)
9. TextReadTool error message must say `"Invalid target. Use one of: lines, heading, codeBlock, anchor, section"`
10. McpTextReadTool `target` param description must match new target list (no `search`, flat heading)
11. ListDirectoriesTool description must be generic (no "library" language)
12. ListFilesTool description must be generic (no "library" language)
13. MoveTool description must be generic (no "library" language)
14. RemoveFileTool description must be generic (no "library" language)
15. Existing TextReadTool tests using old heading format must be updated to flat string
16. Search target test must be converted to expect an error
17. A new test must verify TextReadTool rejects unknown targets with a clear error message
18. All existing TextPatch tests must continue to pass without modification

### Constraints

- No changes to TextPatch runtime logic (only description constant wording)
- No changes to TextPatchToolTests
- Must maintain backward compatibility for all non-deprecated targets
- Follow existing test patterns: `Shouldly` assertions, `TestableXxxTool` wrappers, `IDisposable` cleanup

### Selected Approach

**Approach**: Direct description and signature updates across Domain and MCP layers
**Description**: Update string constants for descriptions/param annotations, simplify TextReadTool heading resolution to flat string, remove search target, and genericize file system tool descriptions. Update tests to match new behavior.
**Rationale**: The design document specifies exact text for every change. All modifications are isolated to string constants and one method signature simplification. No architectural changes needed.
**Trade-offs Accepted**: Removing the `search` target from TextReadTool is a breaking change for agents currently using it, but the design explicitly calls for this since TextInspect's search mode covers the same functionality.

## Implementation Plan

### Domain/Tools/Text/TextPatchTool.cs [edit]

**Purpose**: Minor wording improvement to Description constant cross-reference line.

**TOTAL CHANGES**: 1

**Changes**:
1. Line 28: Change `"4. For text find-and-replace, use TextReplace instead"` to `"4. For text find-and-replace by content match, use TextReplace instead"`

**Implementation Details**:
- Single string change in the `Description` constant
- No import changes
- No signature changes

**Reference Implementation**:
```csharp
// Line 28 change only:
// BEFORE:
4. For text find-and-replace, use TextReplace instead
// AFTER:
4. For text find-and-replace by content match, use TextReplace instead
```

**Migration Pattern**:
```csharp
// BEFORE (line 28):
                                         4. For text find-and-replace, use TextReplace instead

// AFTER (line 28):
                                         4. For text find-and-replace by content match, use TextReplace instead
```

**Dependencies**: None
**Provides**: Updated `TextPatchTool.Description` constant

---

### Domain/Tools/Text/TextReplaceTool.cs [edit]

**Purpose**: Add cross-reference to TextPatch at end of Description constant.

**TOTAL CHANGES**: 1

**Changes**:
1. Lines 38-39: Add a new line at the end of the Description constant before the closing `"""` with text: `"For structural edits by heading, line range, or code block position, use TextPatch instead."`

**Implementation Details**:
- Append one line to the multi-line string constant
- No import changes
- No signature changes

**Reference Implementation**:
```csharp
protected const string Description = """
                                     Performs search-and-replace operations in text files.

                                     Supports:
                                     - Single occurrence replacement (first/last/Nth)
                                     - Replace all occurrences
                                     - Multiline text replacement
                                     - Case-sensitive matching with case-insensitive suggestions
                                     - Optional file hash validation for conflict detection

                                     Parameters:
                                     - filePath: Path to the file (absolute or relative to vault)
                                     - oldText: Exact text to find (case-sensitive)
                                     - newText: Replacement text
                                     - occurrence: "first" (default), "last", "all", or numeric (1-based index)
                                     - expectedHash: Optional 16-char hash for validation

                                     Returns:
                                     - Status, file path, occurrences found/replaced
                                     - Preview of change (before/after, truncated at 200 chars)
                                     - Context lines (3 before/after affected area)
                                     - Affected line range
                                     - File hash for future validation
                                     - Note if other occurrences remain after replacement

                                     Examples:
                                     - Replace first: oldText="v1.0", newText="v2.0"
                                     - Replace last: oldText="TODO", newText="DONE", occurrence="last"
                                     - Replace all: oldText="old", newText="new", occurrence="all"
                                     - Replace 3rd: oldText="item", newText="ITEM", occurrence="3"

                                     For structural edits by heading, line range, or code block position, use TextPatch instead.
                                     """;
```

**Migration Pattern**:
```csharp
// BEFORE (lines 38-39):
                                     - Replace 3rd: oldText="item", newText="ITEM", occurrence="3"
                                     """;

// AFTER:
                                     - Replace 3rd: oldText="item", newText="ITEM", occurrence="3"

                                     For structural edits by heading, line range, or code block position, use TextPatch instead.
                                     """;
```

**Dependencies**: None
**Provides**: Updated `TextReplaceTool.Description` constant

---

### Domain/Tools/Text/TextReadTool.cs [edit]

**Purpose**: Remove search target, unify heading format to flat string, update Description.

**TOTAL CHANGES**: 5

**Changes**:
1. Lines 9-30: Replace entire `Description` constant with updated text that removes `search`, uses flat heading format, and adds cross-reference to TextInspect for search.
2. Lines 78-88: Replace heading case in `ResolveTarget` from `JsonObject` parsing to flat string parsing.
3. Lines 120-125: Remove the `search` case block from `ResolveTarget`.
4. Line 127: Update error message to `"Invalid target. Use one of: lines, heading, codeBlock, anchor, section"`.
5. Lines 130-168: Replace `ResolveHeadingTarget` method with simplified version that takes a single string and always includes children.
6. Lines 221-235: Delete `ResolveSearchTarget` method entirely.

**Implementation Details**:

Imports: No changes needed (all existing imports remain valid).

New `Description` constant:
```csharp
protected const string Description = """
                                     Reads a specific section of a text file. Use after TextInspect to read targeted content.

                                     Targeting methods (use ONE):
                                     - lines: { "start": N, "end": M } - Read specific line range
                                     - heading: "## Section Name" - Read markdown section (includes child headings)
                                     - codeBlock: { "index": N } - Read Nth code block (0-based)
                                     - anchor: "anchor-id" - Read from anchor to next heading
                                     - section: "[marker]" - Read INI-style section

                                     Best practices:
                                     1. Always use TextInspect first to find line numbers or heading names
                                     2. Prefer heading/section targeting for markdown—more stable than line numbers
                                     3. Use line targeting when you need exact control
                                     4. Large sections may be truncated—use narrower targets
                                     5. To search within a file, use TextInspect with search mode

                                     Examples:
                                     - Read lines 50-75: target={ "lines": { "start": 50, "end": 75 } }
                                     - Read Installation section: target={ "heading": "## Installation" }
                                     - Read third code block: target={ "codeBlock": { "index": 2 } }
                                     """;
```

New heading case in `ResolveTarget` (replacing lines 78-88):
```csharp
if (target.TryGetPropertyValue("heading", out var headingNode))
{
    if (!isMarkdown)
    {
        throw new InvalidOperationException("Heading targeting only works with markdown files");
    }

    var heading = headingNode?.GetValue<string>() ?? throw new ArgumentException("heading value required");
    return ResolveHeadingTarget(lines, heading);
}
```

New `ResolveHeadingTarget` (replacing lines 130-168):
```csharp
private static (int Start, int End) ResolveHeadingTarget(string[] lines, string heading)
{
    var structure = MarkdownParser.Parse(lines);
    var normalized = heading.TrimStart('#').Trim();

    var headingIndex = structure.Headings
        .Select((h, i) => (h, i))
        .FirstOrDefault(x => x.h.Text.Equals(normalized, StringComparison.OrdinalIgnoreCase));

    if (headingIndex.h is null)
    {
        var similar = structure.Headings
            .Where(h => h.Text.Contains(normalized.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .Select(h => $"'{new string('#', h.Level)} {h.Text}' (line {h.Line})");

        throw new InvalidOperationException(
            $"Heading '{heading}' not found. Similar: {string.Join(", ", similar)}. Use TextInspect to list all headings.");
    }

    var startLine = headingIndex.h.Line;
    var endLine = MarkdownParser.FindHeadingEnd(structure.Headings, headingIndex.i, lines.Length);

    return (startLine, endLine);
}
```

Updated error message (line 127):
```csharp
throw new ArgumentException("Invalid target. Use one of: lines, heading, codeBlock, anchor, section");
```

Delete `ResolveSearchTarget` method (lines 221-235) entirely.

**Reference Implementation** (full updated file):
```csharp
using System.Text.Json.Nodes;

namespace Domain.Tools.Text;

public class TextReadTool(string vaultPath, string[] allowedExtensions)
{
    protected const string Name = "TextRead";

    protected const string Description = """
                                         Reads a specific section of a text file. Use after TextInspect to read targeted content.

                                         Targeting methods (use ONE):
                                         - lines: { "start": N, "end": M } - Read specific line range
                                         - heading: "## Section Name" - Read markdown section (includes child headings)
                                         - codeBlock: { "index": N } - Read Nth code block (0-based)
                                         - anchor: "anchor-id" - Read from anchor to next heading
                                         - section: "[marker]" - Read INI-style section

                                         Best practices:
                                         1. Always use TextInspect first to find line numbers or heading names
                                         2. Prefer heading/section targeting for markdown—more stable than line numbers
                                         3. Use line targeting when you need exact control
                                         4. Large sections may be truncated—use narrower targets
                                         5. To search within a file, use TextInspect with search mode

                                         Examples:
                                         - Read lines 50-75: target={ "lines": { "start": 50, "end": 75 } }
                                         - Read Installation section: target={ "heading": "## Installation" }
                                         - Read third code block: target={ "codeBlock": { "index": 2 } }
                                         """;

    private const int MaxReturnLines = 200;

    protected JsonNode Run(string filePath, JsonObject target)
    {
        var fullPath = ValidateAndResolvePath(filePath);
        var lines = File.ReadAllLines(fullPath);
        var isMarkdown = Path.GetExtension(fullPath).ToLowerInvariant() is ".md" or ".markdown";

        var (startLine, endLine) = ResolveTarget(target, lines, isMarkdown);

        var actualEnd = Math.Min(endLine, startLine + MaxReturnLines - 1);
        var truncated = endLine > actualEnd;

        var content = string.Join("\n", lines.Skip(startLine - 1).Take(actualEnd - startLine + 1));

        var result = new JsonObject
        {
            ["filePath"] = fullPath,
            ["target"] = target.DeepClone(),
            ["range"] = new JsonObject
            {
                ["startLine"] = startLine,
                ["endLine"] = actualEnd
            },
            ["content"] = content,
            ["truncated"] = truncated
        };

        if (truncated)
        {
            result["totalLines"] = endLine - startLine + 1;
            result["suggestion"] = "Use TextInspect to find specific subsections, or target by narrower line range";
        }

        return result;
    }

    private (int Start, int End) ResolveTarget(JsonObject target, string[] lines, bool isMarkdown)
    {
        if (target.TryGetPropertyValue("lines", out var linesNode) && linesNode is JsonObject linesObj)
        {
            var start = linesObj["start"]?.GetValue<int>() ?? throw new ArgumentException("lines.start required");
            var end = linesObj["end"]?.GetValue<int>() ?? lines.Length;
            return (Math.Max(1, start), Math.Min(lines.Length, end));
        }

        if (target.TryGetPropertyValue("heading", out var headingNode))
        {
            if (!isMarkdown)
            {
                throw new InvalidOperationException("Heading targeting only works with markdown files");
            }

            var heading = headingNode?.GetValue<string>() ?? throw new ArgumentException("heading value required");
            return ResolveHeadingTarget(lines, heading);
        }

        if (target.TryGetPropertyValue("codeBlock", out var codeBlockNode) && codeBlockNode is JsonObject codeBlockObj)
        {
            if (!isMarkdown)
            {
                throw new InvalidOperationException("Code block targeting only works with markdown files");
            }

            var index = codeBlockObj["index"]?.GetValue<int>() ??
                        throw new ArgumentException("codeBlock.index required");
            return ResolveCodeBlockTarget(lines, index);
        }

        if (target.TryGetPropertyValue("anchor", out var anchorNode))
        {
            if (!isMarkdown)
            {
                throw new InvalidOperationException("Anchor targeting only works with markdown files");
            }

            var anchorId = anchorNode?.GetValue<string>() ?? throw new ArgumentException("anchor value required");
            return ResolveAnchorTarget(lines, anchorId);
        }

        if (target.TryGetPropertyValue("section", out var sectionNode))
        {
            var marker = sectionNode?.GetValue<string>() ?? throw new ArgumentException("section value required");
            return ResolveSectionTarget(lines, marker);
        }

        throw new ArgumentException("Invalid target. Use one of: lines, heading, codeBlock, anchor, section");
    }

    private static (int Start, int End) ResolveHeadingTarget(string[] lines, string heading)
    {
        var structure = MarkdownParser.Parse(lines);
        var normalized = heading.TrimStart('#').Trim();

        var headingIndex = structure.Headings
            .Select((h, i) => (h, i))
            .FirstOrDefault(x => x.h.Text.Equals(normalized, StringComparison.OrdinalIgnoreCase));

        if (headingIndex.h is null)
        {
            var similar = structure.Headings
                .Where(h => h.Text.Contains(normalized.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
                .Take(3)
                .Select(h => $"'{new string('#', h.Level)} {h.Text}' (line {h.Line})");

            throw new InvalidOperationException(
                $"Heading '{heading}' not found. Similar: {string.Join(", ", similar)}. Use TextInspect to list all headings.");
        }

        var startLine = headingIndex.h.Line;
        var endLine = MarkdownParser.FindHeadingEnd(structure.Headings, headingIndex.i, lines.Length);

        return (startLine, endLine);
    }

    private static (int Start, int End) ResolveCodeBlockTarget(string[] lines, int index)
    {
        var structure = MarkdownParser.Parse(lines);

        if (index < 0 || index >= structure.CodeBlocks.Count)
        {
            throw new InvalidOperationException(
                $"Code block index {index} out of range. File has {structure.CodeBlocks.Count} code blocks.");
        }

        var block = structure.CodeBlocks[index];
        return (block.StartLine, block.EndLine);
    }

    private static (int Start, int End) ResolveAnchorTarget(string[] lines, string anchorId)
    {
        var structure = MarkdownParser.Parse(lines);

        var anchor = structure.Anchors.FirstOrDefault(a => a.Id.Equals(anchorId, StringComparison.OrdinalIgnoreCase));
        if (anchor is null)
        {
            throw new InvalidOperationException($"Anchor '{anchorId}' not found. Use TextInspect to list all anchors.");
        }

        var nextHeading = structure.Headings.FirstOrDefault(h => h.Line > anchor.Line);
        var endLine = nextHeading?.Line - 1 ?? lines.Length;

        return (anchor.Line, endLine);
    }

    private static (int Start, int End) ResolveSectionTarget(string[] lines, string marker)
    {
        var structure = MarkdownParser.ParsePlainText(lines);

        var sectionIndex = structure.Sections
            .Select((s, i) => (s, i))
            .FirstOrDefault(x => x.s.Marker.Equals(marker, StringComparison.OrdinalIgnoreCase));

        if (sectionIndex.s is null)
        {
            var available = structure.Sections.Select(s => s.Marker).Take(10);
            throw new InvalidOperationException(
                $"Section '{marker}' not found. Available: {string.Join(", ", available)}");
        }

        var startLine = sectionIndex.s.Line;
        var endLine = MarkdownParser.FindSectionEnd(structure.Sections, sectionIndex.i, lines.Length);

        return (startLine, endLine);
    }

    private string ValidateAndResolvePath(string filePath)
    {
        var fullPath = Path.IsPathRooted(filePath)
            ? Path.GetFullPath(filePath)
            : Path.GetFullPath(Path.Combine(vaultPath, filePath));

        if (!fullPath.StartsWith(vaultPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Access denied: path must be within vault directory");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        if (!allowedExtensions.Contains(ext))
        {
            throw new InvalidOperationException(
                $"File type '{ext}' not allowed. Allowed: {string.Join(", ", allowedExtensions)}");
        }

        return fullPath;
    }
}
```

**Test File**: `Tests/Unit/Domain/Text/TextReadToolTests.cs` -- Tests modified in that file entry below.

**Dependencies**: None
**Provides**: Updated `TextReadTool` with flat heading format, no search target, updated Description

---

### McpServerText/McpTools/McpTextPatchTool.cs [edit]

**Purpose**: Fix stale parameter descriptions for `operation` and `target`.

**TOTAL CHANGES**: 2

**Changes**:
1. Line 20: Change `[Description("Operation: 'replace', 'insert', 'delete', or 'replaceLines'")]` to `[Description("Operation: 'replace', 'insert', or 'delete'")]`
2. Lines 22-23: Change `[Description("Target specification as JSON. Use ONE of: lines {start,end}, text, pattern, heading, afterHeading, beforeHeading, codeBlock {index}, section")]` to `[Description("Target specification as JSON. Use ONE of: lines {start,end}, heading, beforeHeading, appendToSection, codeBlock {index}")]`

**Implementation Details**:
- Two `[Description]` attribute string changes
- No import changes
- No signature changes

**Reference Implementation**:
```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerText.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class McpTextPatchTool(McpSettings settings)
    : TextPatchTool(settings.VaultPath, settings.AllowedExtensions)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public CallToolResult McpRun(
        [Description("Path to the text file (absolute or relative to vault)")]
        string filePath,
        [Description("Operation: 'replace', 'insert', or 'delete'")]
        string operation,
        [Description(
            "Target specification as JSON. Use ONE of: lines {start,end}, heading, beforeHeading, appendToSection, codeBlock {index}")]
        string target,
        [Description("New content for replace/insert operations")]
        string? content = null,
        [Description("Match indentation of target line (default: true)")]
        bool preserveIndent = true,
        [Description("Expected file hash for staleness detection. Get from TextInspect structure mode.")]
        string? expectedHash = null)
    {
        var targetObj = JsonNode.Parse(target)?.AsObject() ??
                        throw new ArgumentException("Target must be a valid JSON object");

        return ToolResponse.Create(Run(filePath, operation, targetObj, content, preserveIndent, expectedHash));
    }
}
```

**Dependencies**: `Domain/Tools/Text/TextPatchTool.cs` (inherits from it)
**Provides**: Updated MCP param descriptions for TextPatch

---

### McpServerText/McpTools/McpTextReadTool.cs [edit]

**Purpose**: Update target parameter description to match new target list.

**TOTAL CHANGES**: 1

**Changes**:
1. Lines 20-21: Change `[Description("Target specification as JSON. Use ONE of: lines {start,end}, heading {text,includeChildren}, codeBlock {index}, anchor, section, search {query,contextLines}")]` to `[Description("Target specification as JSON. Use ONE of: lines {start,end}, heading, codeBlock {index}, anchor, section")]`

**Implementation Details**:
- Single `[Description]` attribute string change
- No import changes
- No signature changes

**Reference Implementation**:
```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerText.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class McpTextReadTool(McpSettings settings)
    : TextReadTool(settings.VaultPath, settings.AllowedExtensions)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public CallToolResult McpRun(
        [Description("Path to the text file (absolute or relative to vault)")]
        string filePath,
        [Description(
            "Target specification as JSON. Use ONE of: lines {start,end}, heading, codeBlock {index}, anchor, section")]
        string target)
    {
        var targetObj = JsonNode.Parse(target)?.AsObject()
                        ?? throw new ArgumentException("Target must be a valid JSON object");

        return ToolResponse.Create(Run(filePath, targetObj));
    }
}
```

**Dependencies**: `Domain/Tools/Text/TextReadTool.cs` (inherits from it)
**Provides**: Updated MCP param description for TextRead

---

### Domain/Tools/Files/ListDirectoriesTool.cs [edit]

**Purpose**: Replace media-library-specific description with generic file system language.

**TOTAL CHANGES**: 1

**Changes**:
1. Lines 12-20: Replace entire `Description` constant with generic text.

**Implementation Details**:
- Single string constant replacement
- No import changes
- No signature changes

**Reference Implementation**:
```csharp
protected const string Description = """
                                     Lists all directories as a tree of absolute paths. Returns only directories, not files.
                                     Call once per conversation and reuse the result—directory structure rarely changes.
                                     """;
```

**Migration Pattern**:
```csharp
// BEFORE (lines 12-20):
protected const string Description = """
                                     Lists all directories in the library. It only returns directories, not files.
                                     Must be used to explore the library and find the place in which downloaded
                                     files are currently located and where they should be stored.
                                     This tool returns a list of absolute directories and subdirectories in the
                                     library.
                                     IMPORTANT: The directory structure rarely changes. Call this tool only once
                                     per conversation and reuse the result for subsequent operations.
                                     """;

// AFTER:
protected const string Description = """
                                     Lists all directories as a tree of absolute paths. Returns only directories, not files.
                                     Call once per conversation and reuse the result—directory structure rarely changes.
                                     """;
```

**Dependencies**: None
**Provides**: Updated `ListDirectoriesTool.Description` constant

---

### Domain/Tools/Files/ListFilesTool.cs [edit]

**Purpose**: Replace media-library-specific description with generic file system language.

**TOTAL CHANGES**: 1

**Changes**:
1. Lines 12-18: Replace entire `Description` constant with generic text.

**Implementation Details**:
- Single string constant replacement
- No import changes
- No signature changes

**Reference Implementation**:
```csharp
protected const string Description = """
                                     Lists all files in the specified directory. Returns only files, not directories.
                                     The path must be absolute and derived from the ListDirectories tool.
                                     """;
```

**Migration Pattern**:
```csharp
// BEFORE (lines 12-18):
protected const string Description = """
                                     Lists all files in the specified directory. It only returns files, not
                                     directories.
                                     The path must be absolute and derived from the ListDirectories tool.
                                     Must be used to explore the relevant directories within the library and find
                                     the correct place and name for the downloaded files.
                                     """;

// AFTER:
protected const string Description = """
                                     Lists all files in the specified directory. Returns only files, not directories.
                                     The path must be absolute and derived from the ListDirectories tool.
                                     """;
```

**Dependencies**: None
**Provides**: Updated `ListFilesTool.Description` constant

---

### Domain/Tools/Files/MoveTool.cs [edit]

**Purpose**: Replace media-library-specific description with generic file system language.

**TOTAL CHANGES**: 1

**Changes**:
1. Lines 11-19: Replace entire `Description` constant with generic text.

**Implementation Details**:
- Single string constant replacement
- No import changes
- No signature changes

**Reference Implementation**:
```csharp
protected const string Description = """
                                     Moves and/or renames a file or directory. Both arguments must be absolute paths.
                                     Equivalent to 'mv -T {SourcePath} {DestinationPath}' bash command.
                                     The destination path must not exist. Parent directories are created automatically.
                                     """;
```

**Migration Pattern**:
```csharp
// BEFORE (lines 11-19):
protected const string Description = """
                                     Moves and/or renames a file or directory. Both arguments have to be absolute
                                     paths and must be derived from the ListDirectories tool response.
                                     Equivalent to 'mv -T {SourcePath} {DestinationPath}' bash command.
                                     The destination path MUST NOT exist, otherwise an exception will be thrown.
                                     All necessary parent directories will be created automatically.
                                     PREFER moving entire directories over individual files when possible—it is
                                     faster and avoids missing files.
                                     """;

// AFTER:
protected const string Description = """
                                     Moves and/or renames a file or directory. Both arguments must be absolute paths.
                                     Equivalent to 'mv -T {SourcePath} {DestinationPath}' bash command.
                                     The destination path must not exist. Parent directories are created automatically.
                                     """;
```

**Dependencies**: None
**Provides**: Updated `MoveTool.Description` constant

---

### Domain/Tools/Files/RemoveFileTool.cs [edit]

**Purpose**: Replace media-library-specific description with generic file system language.

**TOTAL CHANGES**: 1

**Changes**:
1. Lines 11-15: Replace entire `Description` constant with generic text.

**Implementation Details**:
- Single string constant replacement
- No import changes
- No signature changes

**Reference Implementation**:
```csharp
protected const string Description = """
                                     Removes a file by moving it to a trash folder.
                                     The path must be absolute and derived from the ListFiles tool response.
                                     """;
```

**Migration Pattern**:
```csharp
// BEFORE (lines 11-15):
protected const string Description = """
                                     Removes a file from the library by moving it to a trash folder.
                                     The path must be an absolute path derived from the ListFiles tool response.
                                     It must start with the library path.
                                     """;

// AFTER:
protected const string Description = """
                                     Removes a file by moving it to a trash folder.
                                     The path must be absolute and derived from the ListFiles tool response.
                                     """;
```

**Dependencies**: None
**Provides**: Updated `RemoveFileTool.Description` constant

---

### Tests/Unit/Domain/Text/TextReadToolTests.cs [edit]

**Purpose**: Update tests for new heading format, convert search test to error test, add unknown target test.

**TOTAL CHANGES**: 4

**Changes**:
1. Lines 55-58 (`Run_HeadingTarget_ReturnsHeadingSection`): Change heading target from `new JsonObject { ["heading"] = new JsonObject { ["text"] = "Setup", ["includeChildren"] = false } }` to `new JsonObject { ["heading"] = "## Setup" }`. Note: with `includeChildren` always true now, the assertion at line 62 (`ShouldNotContain("Config content")`) may need adjustment since the Setup section now always includes children. However, since Setup and Configuration are sibling `##` headings (same level), `FindHeadingEnd` stops at the next same-or-higher level heading, so "Config content" is still excluded. The assertion remains valid.
2. Lines 153-161 (`Run_HeadingNotFound_ThrowsWithSuggestions`): Change heading target from `new JsonObject { ["heading"] = new JsonObject { ["text"] = "Instalation" } }` to `new JsonObject { ["heading"] = "## Instalation" }`.
3. Lines 109-129 (`Run_SearchTarget_ReturnsContextAroundMatch`): Convert to `Run_SearchTarget_ThrowsArgumentException` that verifies search target is rejected with an error.
4. Add new test `Run_UnknownTarget_ThrowsWithValidTargetList` that verifies an unknown target key produces an error message listing valid targets.

**Implementation Details**:

No new imports needed. All existing imports remain valid.

**Reference Implementation** (full updated test file):
```csharp
using System.Text.Json.Nodes;
using Domain.Tools.Text;
using Shouldly;

namespace Tests.Unit.Domain.Text;

public class TextReadToolTests : IDisposable
{
    private readonly string _testDir;
    private readonly TestableTextReadTool _tool;

    public TextReadToolTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"text-read-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
        _tool = new TestableTextReadTool(_testDir, [".md", ".txt"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    [Fact]
    public void Run_LinesTarget_ReturnsSpecifiedLines()
    {
        var content = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"Line {i}"));
        var filePath = CreateTestFile("test.md", content);

        var target = new JsonObject { ["lines"] = new JsonObject { ["start"] = 3, ["end"] = 5 } };
        var result = _tool.TestRun(filePath, target);

        result["content"]!.ToString().ShouldBe("Line 3\nLine 4\nLine 5");
        result["range"]!["startLine"]!.GetValue<int>().ShouldBe(3);
        result["range"]!["endLine"]!.GetValue<int>().ShouldBe(5);
    }

    [Fact]
    public void Run_HeadingTarget_ReturnsHeadingSection()
    {
        var content = """
                      # Introduction
                      Intro content
                      ## Setup
                      Setup content line 1
                      Setup content line 2
                      ## Configuration
                      Config content
                      """;
        var filePath = CreateTestFile("doc.md", content);

        var target = new JsonObject { ["heading"] = "## Setup" };
        var result = _tool.TestRun(filePath, target);

        result["content"]!.ToString().ShouldContain("Setup content");
        result["content"]!.ToString().ShouldNotContain("Config content");
    }

    [Fact]
    public void Run_CodeBlockTarget_ReturnsCodeBlock()
    {
        var content = """
                      # Examples
                      ```csharp
                      var x = 1;
                      var y = 2;
                      ```
                      Some text
                      ```python
                      print("hello")
                      ```
                      """;
        var filePath = CreateTestFile("code.md", content);

        var target = new JsonObject { ["codeBlock"] = new JsonObject { ["index"] = 1 } };
        var result = _tool.TestRun(filePath, target);

        result["content"]!.ToString().ShouldContain("python");
        result["content"]!.ToString().ShouldContain("print");
    }

    [Fact]
    public void Run_SectionTarget_ReturnsIniSection()
    {
        var content = """
                      [database]
                      host=localhost
                      port=5432

                      [cache]
                      enabled=true
                      """;
        var filePath = CreateTestFile("config.txt", content);

        var target = new JsonObject { ["section"] = "[database]" };
        var result = _tool.TestRun(filePath, target);

        result["content"]!.ToString().ShouldContain("host=localhost");
        result["content"]!.ToString().ShouldNotContain("enabled=true");
    }

    [Fact]
    public void Run_SearchTarget_ThrowsArgumentException()
    {
        var content = """
                      Line 1
                      Line 2
                      Target text here
                      Line 4
                      Line 5
                      """;
        var filePath = CreateTestFile("search.md", content);

        var target = new JsonObject
        {
            ["search"] = new JsonObject { ["query"] = "Target", ["contextLines"] = 2 }
        };

        var ex = Should.Throw<ArgumentException>(() => _tool.TestRun(filePath, target));
        ex.Message.ShouldContain("Invalid target");
    }

    [Fact]
    public void Run_LargeSection_IsTruncated()
    {
        var content = string.Join("\n", Enumerable.Range(1, 500).Select(i => $"Line {i}"));
        var filePath = CreateTestFile("large.md", content);

        var target = new JsonObject { ["lines"] = new JsonObject { ["start"] = 1, ["end"] = 500 } };
        var result = _tool.TestRun(filePath, target);

        result["truncated"]!.GetValue<bool>().ShouldBeTrue();
        result["suggestion"]!.ToString().ShouldNotBeEmpty();
    }

    [Fact]
    public void Run_HeadingNotFound_ThrowsWithSuggestions()
    {
        var content = """
                      # Introduction
                      ## Installation
                      ## Configuration
                      """;
        var filePath = CreateTestFile("doc.md", content);

        var target = new JsonObject { ["heading"] = "## Instalation" }; // Typo

        var ex = Should.Throw<InvalidOperationException>(() => _tool.TestRun(filePath, target));
        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public void Run_UnknownTarget_ThrowsWithValidTargetList()
    {
        var content = "Some content";
        var filePath = CreateTestFile("test.md", content);

        var target = new JsonObject { ["unknown"] = "value" };

        var ex = Should.Throw<ArgumentException>(() => _tool.TestRun(filePath, target));
        ex.Message.ShouldContain("Invalid target");
        ex.Message.ShouldContain("lines");
        ex.Message.ShouldContain("heading");
        ex.Message.ShouldContain("codeBlock");
        ex.Message.ShouldContain("anchor");
        ex.Message.ShouldContain("section");
        ex.Message.ShouldNotContain("search");
    }

    private string CreateTestFile(string name, string content)
    {
        var path = Path.Combine(_testDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private class TestableTextReadTool(string vaultPath, string[] allowedExtensions)
        : TextReadTool(vaultPath, allowedExtensions)
    {
        public JsonNode TestRun(string filePath, JsonObject target)
        {
            return Run(filePath, target);
        }
    }
}
```

**Dependencies**: `Domain/Tools/Text/TextReadTool.cs` (tests exercise it)
**Provides**: Test coverage for updated TextReadTool behavior

---

## Dependency Graph

> Files in the same phase can execute in parallel.

| Phase | File | Action | Depends On |
|-------|------|--------|------------|
| 1 | `Domain/Tools/Files/ListDirectoriesTool.cs` | edit | -- |
| 1 | `Domain/Tools/Files/ListFilesTool.cs` | edit | -- |
| 1 | `Domain/Tools/Files/MoveTool.cs` | edit | -- |
| 1 | `Domain/Tools/Files/RemoveFileTool.cs` | edit | -- |
| 1 | `Domain/Tools/Text/TextPatchTool.cs` | edit | -- |
| 1 | `Domain/Tools/Text/TextReplaceTool.cs` | edit | -- |
| 2 | `Tests/Unit/Domain/Text/TextReadToolTests.cs` | edit | -- |
| 3 | `Domain/Tools/Text/TextReadTool.cs` | edit | `Tests/Unit/Domain/Text/TextReadToolTests.cs` |
| 4 | `McpServerText/McpTools/McpTextPatchTool.cs` | edit | `Domain/Tools/Text/TextPatchTool.cs` |
| 4 | `McpServerText/McpTools/McpTextReadTool.cs` | edit | `Domain/Tools/Text/TextReadTool.cs` |

## Exit Criteria

### Test Commands
```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TextReadToolTests"
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TextPatchToolTests"
dotnet build McpServerText/McpServerText.csproj
```

### Success Conditions
- [ ] All TextReadToolTests pass (exit code 0)
- [ ] All TextPatchToolTests pass (exit code 0) -- no modifications, just verify no regressions
- [ ] McpServerText project builds successfully
- [ ] `Run_SearchTarget_ThrowsArgumentException` test passes (search target rejected)
- [ ] `Run_UnknownTarget_ThrowsWithValidTargetList` test passes (error lists valid targets, no "search")
- [ ] `Run_HeadingTarget_ReturnsHeadingSection` test passes with flat string heading format
- [ ] `Run_HeadingNotFound_ThrowsWithSuggestions` test passes with flat string heading format
- [ ] All requirements (1-18) are satisfied

### Verification Script
```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TextReadToolTests" && dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TextPatchToolTests" && dotnet build McpServerText/McpServerText.csproj
```
