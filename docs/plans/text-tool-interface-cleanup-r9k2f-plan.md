# Text Tool Interface Cleanup Plan

## Summary

Merge TextPatch and TextReplace into a single TextEdit tool, strip TextInspect to structure-only mode, add single-file search to TextSearch, and standardize parameter names across all text/file tools. This reduces the tool count from 10 to 9, eliminates duplicate capabilities, and resolves 6 interface issues that confuse agents.

## Files

> **Note**: This is the canonical file list.

### Files to Edit
- `Domain/Tools/Text/TextInspectTool.cs`
- `Domain/Tools/Text/TextSearchTool.cs`
- `Domain/Tools/Text/TextReadTool.cs`
- `Domain/Tools/Text/TextCreateTool.cs`
- `Domain/Tools/Files/ListFilesTool.cs`
- `Domain/Tools/Files/RemoveFileTool.cs`
- `McpServerText/McpTools/McpTextInspectTool.cs`
- `McpServerText/McpTools/McpTextSearchTool.cs`
- `McpServerText/McpTools/McpTextReadTool.cs`
- `McpServerText/McpTools/McpTextListFilesTool.cs`
- `McpServerText/McpTools/McpRemoveFileTool.cs`
- `McpServerText/Modules/ConfigModule.cs`
- `Tests/Unit/Domain/Text/TextInspectToolTests.cs`
- `Tests/Unit/Domain/Text/TextSearchToolTests.cs`
- `Tests/Unit/Domain/Text/TextReadToolTests.cs`

### Files to Create
- `Domain/Tools/Text/TextEditTool.cs`
- `McpServerText/McpTools/McpTextEditTool.cs`
- `Tests/Unit/Domain/Text/TextEditToolTests.cs`

### Files to Delete
- `Domain/Tools/Text/TextPatchTool.cs`
- `Domain/Tools/Text/TextReplaceTool.cs`
- `McpServerText/McpTools/McpTextPatchTool.cs`
- `McpServerText/McpTools/McpTextReplaceTool.cs`
- `Tests/Unit/Domain/Text/TextPatchToolTests.cs`
- `Tests/Unit/Domain/Text/TextReplaceToolTests.cs`

## Code Context

### Domain Layer - Text Tools

**TextPatchTool** (`Domain/Tools/Text/TextPatchTool.cs`):
- Extends `TextToolBase(vaultPath, allowedExtensions)` at line 5
- `protected const string Name = "TextPatch"` at line 7
- `protected JsonNode Run(string filePath, string operation, JsonObject target, string? content = null, bool preserveIndent = true, string? expectedHash = null)` at line 38
- Operations: replace, insert, delete (line 59-66)
- Target resolution via `ResolveTarget()` at line 147: supports `lines`, `heading`, `appendToSection`, `beforeHeading`, `codeBlock`
- Currently throws for `text` target at line 208-211, directing to TextReplace
- Private helpers: `ValidateOperation`, `ValidateLineRange`, `FindHeadingTarget`, `FindHeadingIndex`, `FindCodeBlockTarget`, `ApplyReplace`, `ApplyInsert`, `ApplyDelete`, `GetIndent`

**TextReplaceTool** (`Domain/Tools/Text/TextReplaceTool.cs`):
- Extends `TextToolBase(vaultPath, allowedExtensions)` at line 5
- `protected const string Name = "TextReplace"` at line 7
- `protected JsonNode Run(string filePath, string oldText, string newText, string occurrence = "first", string? expectedHash = null)` at line 43
- Content-matching logic: `FindAllOccurrences`, `FindCaseInsensitiveSuggestion`, `ApplyReplacement`, `ComputeAffectedLines`, `GetContextLines`
- Supports occurrence: "first", "last", "all", or numeric 1-based index

**TextInspectTool** (`Domain/Tools/Text/TextInspectTool.cs`):
- Extends `TextToolBase(vaultPath, allowedExtensions)` at line 6
- `protected JsonNode Run(string filePath, string mode = "structure", string? query = null, bool regex = false, int context = 0)` at line 31
- Three modes: "structure" (line 49), "search" (line 156), "lines" (line 240)
- Structure mode delegates to `MarkdownParser.Parse()` and `MarkdownParser.ParsePlainText()`
- Search mode duplicates TextSearch single-file capability
- Lines mode overlaps with TextRead lines target

**TextSearchTool** (`Domain/Tools/Text/TextSearchTool.cs`):
- Does NOT extend TextToolBase (line 6) -- standalone class with `(string vaultPath, string[] allowedExtensions)` constructor
- `protected JsonNode Run(string query, bool regex = false, string? filePattern = null, string path = "/", int maxResults = 50, int contextLines = 1)` at line 42
- Directory-based search only; no single-file search capability
- Has its own `ResolvePath()` method (line 188), `IsAllowedExtension()` (line 77)

**TextReadTool** (`Domain/Tools/Text/TextReadTool.cs`):
- Does NOT extend TextToolBase (line 5) -- standalone class with own `ValidateAndResolvePath()` at line 198
- `protected JsonNode Run(string filePath, JsonObject target)` at line 34
- Description at line 24 says "To search within a file, use TextInspect with search mode" -- needs update

**TextToolBase** (`Domain/Tools/Text/TextToolBase.cs`):
- Abstract class with `ValidateAndResolvePath()`, `ComputeFileHash()`, `ValidateExpectedHash()`

**TextCreateTool** (`Domain/Tools/Text/TextCreateTool.cs`):
- Error message at line 63 says "Use TextPatch to modify existing files" -- needs update to "TextEdit"

### Domain Layer - File Tools

**ListFilesTool** (`Domain/Tools/Files/ListFilesTool.cs`):
- Parameter is `string path` at line 17
- Description says "path must be absolute" at line 14

**RemoveFileTool** (`Domain/Tools/Files/RemoveFileTool.cs`):
- Parameter is `string path` at line 16
- Description says "path must be absolute" at line 13

### MCP Layer

**McpTextPatchTool** (`McpServerText/McpTools/McpTextPatchTool.cs`):
- Inherits `TextPatchTool(settings.VaultPath, settings.AllowedExtensions)` at line 13
- `McpRun(string filePath, string operation, string target, string? content, bool preserveIndent, string? expectedHash)` at line 17

**McpTextReplaceTool** (`McpServerText/McpTools/McpTextReplaceTool.cs`):
- Inherits `TextReplaceTool(settings.VaultPath, settings.AllowedExtensions)` at line 11
- `McpRun(string filePath, string oldText, string newText, string occurrence, string? expectedHash)` at line 16

**McpTextSearchTool** (`McpServerText/McpTools/McpTextSearchTool.cs`):
- Parameter `path` at line 24 with description "Directory to search in"

**McpTextListFilesTool** (`McpServerText/McpTools/McpTextListFilesTool.cs`):
- Parameter `path` at line 20 with description "Absolute path to the directory"

**McpRemoveFileTool** (`McpServerText/McpTools/McpRemoveFileTool.cs`):
- Parameter `path` at line 20 with description "Absolute path to the file"

**ConfigModule** (`McpServerText/Modules/ConfigModule.cs`):
- Registers `McpTextPatchTool` at line 58, no `McpTextReplaceTool` registered
- Need to replace `McpTextPatchTool` with `McpTextEditTool`

### Test Layer

**TextPatchToolTests** (`Tests/Unit/Domain/Text/TextPatchToolTests.cs`):
- `TestableTextPatchTool` wrapper at line 234
- 13 tests covering: insert before heading, delete lines, replace code block, heading not found, missing content, path outside vault, text target throws, pattern target throws, section target throws, replaceLines operation throws, append to section, append last section, append non-markdown, append heading not found

**TextReplaceToolTests** (`Tests/Unit/Domain/Text/TextReplaceToolTests.cs`):
- `TestableTextReplaceTool` wrapper at line 230
- 14 tests covering: single occurrence, multiple first/last/all/nth, nth exceeds, text not found, case-insensitive suggestion, multiline, context lines, file hash, expected hash match/mismatch, note, path outside vault, disallowed extension

**TextInspectToolTests** (`Tests/Unit/Domain/Text/TextInspectToolTests.cs`):
- Tests for structure, search, and lines modes
- Tests to update: remove search mode tests, remove lines mode tests

**TextSearchToolTests** (`Tests/Unit/Domain/Text/TextSearchToolTests.cs`):
- Tests for multi-file search, file pattern, subdirectory, regex, heading, context, max results, case insensitive, no matches, skips disallowed, relative paths

## External Context

N/A - All changes are internal refactoring of existing domain tools. No external libraries or APIs are involved beyond the existing ModelContextProtocol SDK already in use.

## Architectural Narrative

### Task

Merge TextPatch and TextReplace into a unified TextEdit tool, strip TextInspect to structure-only, add single-file search to TextSearch, and standardize parameter naming across all text and file tools. This addresses 6 interface issues documented in the design.

### Architecture

The McpServerText project follows a two-layer pattern:
1. **Domain layer** (`Domain/Tools/Text/`): Business logic tools with `protected` Run methods
2. **MCP layer** (`McpServerText/McpTools/`): Thin wrappers that inherit domain tools, parse JSON parameters, and return `CallToolResult` via `ToolResponse.Create()`

Each tool has a `Name` and `Description` const in the domain class. The MCP wrapper uses `[McpServerTool(Name = Name)]` and `[Description(Description)]` attributes. Error handling is centralized in `ConfigModule.cs` via `AddCallToolFilter`.

### Selected Context

- `Domain/Tools/Text/TextToolBase.cs`: Base class providing `ValidateAndResolvePath()`, `ComputeFileHash()`, `ValidateExpectedHash()` -- TextEditTool will inherit this
- `Domain/Tools/Text/TextPatchTool.cs`: Contains positional targeting logic (lines, heading, beforeHeading, appendToSection, codeBlock) and apply operations (replace, insert, delete)
- `Domain/Tools/Text/TextReplaceTool.cs`: Contains content-matching logic (find/replace by text, occurrence selection, case-insensitive suggestions)
- `Domain/Tools/Text/MarkdownParser.cs`: Static parser for markdown structure, used by TextPatchTool and TextInspectTool
- `Infrastructure/Utils/ToolResponse.cs`: Utility for wrapping responses as `CallToolResult`
- `McpServerText/Settings/McpSettings.cs`: Settings record with VaultPath and AllowedExtensions

### Relationships

- `TextEditTool` (new) will compose logic from both `TextPatchTool` and `TextReplaceTool`, inheriting from `TextToolBase`
- `McpTextEditTool` (new) will inherit `TextEditTool` and expose parameters via MCP
- `TextSearchTool` gains single-file capability, making `TextInspect` search mode redundant
- `TextRead` lines target makes `TextInspect` lines mode redundant
- `ConfigModule` wires tool registrations -- must replace Patch with Edit, remove Replace

### External Context

No external documentation needed. Changes are purely internal refactoring.

### Implementation Notes

1. **TextEditTool composition strategy**: The new `TextEdit` tool will have a single `Run` method that accepts `target` as a `JsonObject`. If the target contains a `text` key, it delegates to content-matching logic (from TextReplace). Otherwise, it delegates to positional logic (from TextPatch). The `occurrence` parameter is only relevant for `text` targets.

2. **Parameter unification**: The `TextEdit` `Run` method signature will be:
   ```csharp
   protected JsonNode Run(string filePath, string operation, JsonObject target, string? content = null,
       string? occurrence = null, bool preserveIndent = true, string? expectedHash = null)
   ```
   When target has `text` key: extract oldText from target, use content as newText, occurrence controls which match. Operation must be "replace" for text targets (insert/delete don't make sense for content matching).

3. **TextSearchTool single-file**: When `filePath` is provided, skip directory enumeration and search only that file. The `directoryPath` and `filePattern` params are ignored.

4. **Parameter renames**: `path` -> `directoryPath` in ListFilesTool and TextSearchTool. `path` -> `filePath` in RemoveFileTool. These are rename-only changes to the `Run` method parameter names and corresponding MCP `[Description]` attributes.

5. **TextInspect stripping**: Remove `mode`, `query`, `regex`, `context` parameters. The `Run` method becomes just `Run(string filePath)` returning only structure data.

6. **Cross-references in descriptions**: TextEdit will reference TextInspect and TextSearch. TextRead will reference TextInspect. TextSearch will reference TextEdit.

### Ambiguities

1. **TextEdit operation validation for text targets**: The design says TextEdit supports replace/insert/delete operations. For `text` targets, only "replace" is meaningful. Decision: throw `ArgumentException` if operation is "insert" or "delete" with a text target.

2. **TextSearchTool filePath validation**: When filePath is provided for single-file search, should it use TextToolBase-style validation? Decision: Add a `ValidateAndResolveSingleFilePath` private method since TextSearchTool does not extend TextToolBase.

### Requirements

1. TextPatch and TextReplace must be merged into a single TextEdit tool
2. TextEdit must support all 6 target keys: lines, heading, beforeHeading, appendToSection, codeBlock, text
3. TextEdit with `text` target must support occurrence parameter (first, last, all, numeric)
4. TextInspect must be stripped to structure-only mode (no mode, query, regex, context params)
5. TextSearch must support optional `filePath` parameter for single-file search
6. TextSearch `path` parameter must be renamed to `directoryPath`
7. ListFilesTool `path` parameter must be renamed to `directoryPath`
8. RemoveFileTool `path` parameter must be renamed to `filePath`
9. Tool descriptions must include cross-references (TextEdit -> TextInspect/TextSearch, TextRead -> TextInspect, TextSearch -> TextEdit)
10. TextCreateTool error message must reference TextEdit instead of TextPatch
11. All existing TextPatch and TextReplace tests must be preserved in TextEditTool tests
12. ConfigModule must register McpTextEditTool and remove McpTextPatchTool (McpTextReplaceTool was not registered)
13. Old TextPatchTool, TextReplaceTool, McpTextPatchTool, McpTextReplaceTool, and their test files must be deleted

### Constraints

- Domain layer must not reference Infrastructure or Agent namespaces
- MCP tools must inherit from their Domain counterpart
- Error handling stays centralized in ConfigModule's AddCallToolFilter
- Test naming convention: `{Method}_{Scenario}_{ExpectedResult}`
- Use Shouldly assertions
- Use testable wrapper pattern for protected `Run` methods
- File-scoped namespaces, primary constructors

### Selected Approach

**Approach**: Composition by delegation within TextEditTool
**Description**: TextEditTool contains all logic from both TextPatchTool and TextReplaceTool in a single class. The `Run` method inspects the `target` JsonObject: if it has a `text` key, it runs content-matching replace logic; otherwise it runs positional operation logic. All private helper methods from both tools are merged into TextEditTool.
**Rationale**: This is the simplest approach -- a single class replaces two. The tools already share `TextToolBase`, so merging them is natural. No new interfaces or abstractions needed.
**Trade-offs Accepted**: TextEditTool will be a larger file (~400 lines) combining both tools' logic. This is acceptable because the two code paths (positional vs content-match) are cleanly separated by the target key dispatch.

## Implementation Plan

### Domain/Tools/Text/TextEditTool.cs [create]

**Purpose**: Unified file editing tool that merges positional targeting (from TextPatchTool) and content-matching (from TextReplaceTool) into one tool.

**TOTAL CHANGES**: 1 (new file creation)

**Changes**:
1. Create new file with complete TextEditTool class

**Implementation Details**:
- Inherits `TextToolBase(string vaultPath, string[] allowedExtensions)`
- `Name = "TextEdit"`, `Description` documents all 6 target keys and 3 operations
- `Run(string filePath, string operation, JsonObject target, string? content = null, string? occurrence = null, bool preserveIndent = true, string? expectedHash = null)` dispatches on target key
- When target has `text` key: validate operation is "replace", extract oldText from `target["text"]`, use `content` as newText, delegate to content-match logic
- When target has positional keys: delegate to positional operation logic (same as TextPatchTool)
- All private methods from both TextPatchTool and TextReplaceTool are included

**Reference Implementation**:
```csharp
using System.Text.Json.Nodes;

namespace Domain.Tools.Text;

public class TextEditTool(string vaultPath, string[] allowedExtensions) : TextToolBase(vaultPath, allowedExtensions)
{
    protected const string Name = "TextEdit";

    protected const string Description = """
                                         Modifies a text or markdown file using positional targeting or content matching.

                                         Use TextInspect first to find line numbers and headings. To search within files, use TextSearch.

                                         Operations:
                                         - 'replace': Replace targeted content with new content
                                         - 'insert': Insert new content at target location (positional targets only)
                                         - 'delete': Remove targeted content (positional targets only)

                                         Targeting (use ONE key in target JSON):
                                         - lines: { "start": N, "end": M } - Target specific line range
                                         - heading: "## Title" - Target a markdown heading line
                                         - beforeHeading: "## Title" - Position before a heading (for insert)
                                         - appendToSection: "## Title" - Append to end of a markdown section (for insert)
                                         - codeBlock: { "index": N } - Target Nth code block content (0-based)
                                         - text: "exact match" - Find and replace by content match (case-sensitive)

                                         For text targets:
                                         - operation must be 'replace'
                                         - Use occurrence param: 'first' (default), 'last', 'all', or numeric 1-based index
                                         - Case-sensitive matching with case-insensitive suggestions on failure

                                         Examples:
                                         - Replace heading: operation="replace", target={ "heading": "## Old" }, content="## New"
                                         - Insert before heading: operation="insert", target={ "beforeHeading": "## Setup" }, content="## New Section\n"
                                         - Append to section: operation="insert", target={ "appendToSection": "## Setup" }, content="New content\n"
                                         - Delete lines 50-55: operation="delete", target={ "lines": { "start": 50, "end": 55 } }
                                         - Replace code block: operation="replace", target={ "codeBlock": { "index": 0 } }, content="new code..."
                                         - Find and replace text: operation="replace", target={ "text": "old text" }, content="new text"
                                         - Replace all occurrences: operation="replace", target={ "text": "old" }, content="new", occurrence="all"
                                         """;

    protected JsonNode Run(string filePath, string operation, JsonObject target, string? content = null,
        string? occurrence = null, bool preserveIndent = true, string? expectedHash = null)
    {
        var fullPath = ValidateAndResolvePath(filePath);
        var lines = File.ReadAllLines(fullPath);
        ValidateExpectedHash(lines, expectedHash);

        if (target.TryGetPropertyValue("text", out var textNode))
        {
            return RunTextReplace(fullPath, lines, textNode?.GetValue<string>()
                ?? throw new ArgumentException("text target value required"),
                content ?? throw new ArgumentException("Content required for text replace"),
                operation, occurrence ?? "first");
        }

        return RunPositionalEdit(fullPath, lines, operation, target, content, preserveIndent);
    }

    private JsonNode RunTextReplace(string fullPath, string[] originalLines, string oldText, string newText,
        string operation, string occurrence)
    {
        if (operation.ToLowerInvariant() != "replace")
        {
            throw new ArgumentException(
                $"Text target only supports 'replace' operation, not '{operation}'. Use positional targets for insert/delete.");
        }

        var content = File.ReadAllText(fullPath);

        var positions = FindAllOccurrences(content, oldText);

        if (positions.Count == 0)
        {
            var caseSuggestion = FindCaseInsensitiveSuggestion(content, oldText);
            if (caseSuggestion is not null)
            {
                throw new InvalidOperationException(
                    $"Text '{oldText}' not found (case-sensitive). Did you mean '{caseSuggestion}'?");
            }

            throw new InvalidOperationException($"Text '{oldText}' not found in file.");
        }

        var (replacedContent, replacedCount, replacementPosition) =
            ApplyTextReplacement(content, oldText, newText, occurrence, positions);

        var tempPath = fullPath + ".tmp";
        File.WriteAllText(tempPath, replacedContent);
        File.Move(tempPath, fullPath, overwrite: true);

        var (startLine, endLine) = ComputeAffectedLines(content, replacementPosition, oldText.Length);

        var updatedLines = File.ReadAllLines(fullPath);
        var fileHash = ComputeFileHash(updatedLines);

        var result = new JsonObject
        {
            ["status"] = "success",
            ["filePath"] = fullPath,
            ["occurrencesFound"] = positions.Count,
            ["occurrencesReplaced"] = replacedCount,
            ["affectedLines"] = new JsonObject
            {
                ["start"] = startLine,
                ["end"] = endLine
            },
            ["fileHash"] = fileHash
        };

        var beforeText = oldText.Length > 200 ? oldText[..200] + "..." : oldText;
        var afterText = newText.Length > 200 ? newText[..200] + "..." : newText;
        result["preview"] = new JsonObject
        {
            ["before"] = beforeText,
            ["after"] = afterText
        };

        var contextLines = GetTextReplaceContextLines(updatedLines, startLine, endLine);
        result["context"] = new JsonArray(contextLines.Select(l => JsonValue.Create(l)).ToArray());

        if (replacedCount < positions.Count)
        {
            var remaining = positions.Count - replacedCount;
            result["note"] = $"{remaining} other occurrence(s) remain at other locations";
        }

        return result;
    }

    private JsonNode RunPositionalEdit(string fullPath, string[] originalLines, string operation, JsonObject target,
        string? content, bool preserveIndent)
    {
        var linesList = originalLines.ToList();
        var isMarkdown = Path.GetExtension(fullPath).ToLowerInvariant() is ".md" or ".markdown";

        ValidateOperation(operation, content);

        var (startLine, endLine, matchedText) = ResolveTarget(target, linesList, isMarkdown);
        var originalTotalLines = originalLines.Length;
        var originalLineCount = linesList.Count;

        string? previousContent = null;
        if (startLine > 0 && endLine > 0)
        {
            previousContent = string.Join("\n", linesList.Skip(startLine - 1).Take(endLine - startLine + 1));
        }

        var result = operation.ToLowerInvariant() switch
        {
            "replace" => ApplyReplace(linesList, startLine, endLine, matchedText, content!, preserveIndent),
            "insert" => ApplyInsert(linesList, target, startLine, content!, preserveIndent),
            "delete" => ApplyDelete(linesList, startLine, endLine),
            _ => throw new ArgumentException(
                $"Invalid operation '{operation}'. Must be 'replace', 'insert', or 'delete'.")
        };

        var tempPath = fullPath + ".tmp";
        File.WriteAllLines(tempPath, linesList);
        File.Move(tempPath, fullPath, overwrite: true);

        var updatedLines = File.ReadAllLines(fullPath);
        var newEndLine = startLine + (updatedLines.Length - originalTotalLines) + (endLine - startLine);

        var contextBefore = new JsonArray();
        var beforeStart = Math.Max(0, startLine - 3 - 1);
        var beforeEnd = startLine - 1;
        for (var i = beforeStart; i < beforeEnd; i++)
        {
            if (i >= 0 && i < updatedLines.Length)
            {
                contextBefore.Add(updatedLines[i]);
            }
        }

        var contextAfter = new JsonArray();
        var afterStart = newEndLine;
        var afterEnd = Math.Min(updatedLines.Length, afterStart + 3);
        for (var i = afterStart; i < afterEnd; i++)
        {
            if (i >= 0 && i < updatedLines.Length)
            {
                contextAfter.Add(updatedLines[i]);
            }
        }

        result["status"] = "success";
        result["filePath"] = fullPath;
        result["operation"] = operation;
        result["affectedLines"] = new JsonObject
        {
            ["start"] = startLine,
            ["end"] = endLine
        };
        result["linesDelta"] = linesList.Count - originalLineCount;
        result["context"] = new JsonObject
        {
            ["beforeLines"] = contextBefore,
            ["afterLines"] = contextAfter
        };
        result["fileHash"] = ComputeFileHash(updatedLines);

        if (previousContent is not null && previousContent.Length < 500)
        {
            result["preview"] = new JsonObject
            {
                ["before"] = previousContent.Length > 200 ? previousContent[..200] + "..." : previousContent,
                ["after"] = result["newContent"]?.GetValue<string>() ?? ""
            };
        }

        if (linesList.Count != originalLineCount)
        {
            result["note"] = $"File now has {linesList.Count} lines (was {originalLineCount})";
        }

        return result;
    }

    // --- Positional edit helpers (from TextPatchTool) ---

    private static void ValidateOperation(string operation, string? content)
    {
        var op = operation.ToLowerInvariant();
        if (op is not ("replace" or "insert" or "delete"))
        {
            throw new ArgumentException($"Invalid operation '{operation}'. Must be 'replace', 'insert', or 'delete'.");
        }

        if (op is "replace" or "insert" && string.IsNullOrEmpty(content))
        {
            throw new ArgumentException($"Content required for '{operation}' operation");
        }
    }

    private (int StartLine, int EndLine, string? MatchedText) ResolveTarget(JsonObject target, List<string> lines,
        bool isMarkdown)
    {
        if (target.TryGetPropertyValue("lines", out var linesNode) && linesNode is JsonObject linesObj)
        {
            var start = linesObj["start"]?.GetValue<int>() ?? throw new ArgumentException("lines.start required");
            var end = linesObj["end"]?.GetValue<int>() ?? start;
            ValidateLineRange(start, end, lines.Count);
            return (start, end, null);
        }

        if (target.TryGetPropertyValue("heading", out var headingNode))
        {
            if (!isMarkdown)
            {
                throw new InvalidOperationException("Heading targeting only works with markdown files");
            }

            var heading = headingNode?.GetValue<string>() ?? throw new ArgumentException("heading value required");
            return FindHeadingTarget(lines, heading);
        }

        if (target.TryGetPropertyValue("appendToSection", out var appendNode))
        {
            if (!isMarkdown)
            {
                throw new InvalidOperationException("appendToSection targeting only works with markdown files");
            }

            var heading = appendNode?.GetValue<string>() ?? throw new ArgumentException("appendToSection value required");
            var structure = MarkdownParser.Parse(lines.ToArray());
            var headingIndex = FindHeadingIndex(structure, heading);
            var endLine = MarkdownParser.FindHeadingEnd(structure.Headings, headingIndex, lines.Count);
            return (endLine, endLine, null);
        }

        if (target.TryGetPropertyValue("beforeHeading", out var beforeNode))
        {
            if (!isMarkdown)
            {
                throw new InvalidOperationException("Heading targeting only works with markdown files");
            }

            var heading = beforeNode?.GetValue<string>() ?? throw new ArgumentException("beforeHeading value required");
            var (line, _, _) = FindHeadingTarget(lines, heading);
            return (line - 1, line - 1, null);
        }

        if (target.TryGetPropertyValue("codeBlock", out var codeBlockNode) && codeBlockNode is JsonObject codeBlockObj)
        {
            if (!isMarkdown)
            {
                throw new InvalidOperationException("Code block targeting only works with markdown files");
            }

            var index = codeBlockObj["index"]?.GetValue<int>() ??
                        throw new ArgumentException("codeBlock.index required");
            return FindCodeBlockTarget(lines, index);
        }

        throw new ArgumentException(
            "Invalid target. Use one of: lines, heading, beforeHeading, appendToSection, codeBlock, text");
    }

    private static void ValidateLineRange(int start, int end, int totalLines)
    {
        if (start < 1 || start > totalLines)
        {
            throw new ArgumentException($"Start line {start} out of range. File has {totalLines} lines.");
        }

        if (end < start || end > totalLines)
        {
            throw new ArgumentException($"End line {end} out of range. Must be >= {start} and <= {totalLines}.");
        }
    }

    private static (int, int, string?) FindHeadingTarget(List<string> lines, string heading)
    {
        var normalized = heading.TrimStart('#').Trim();

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!line.StartsWith('#'))
            {
                continue;
            }

            if (line.Equals(heading, StringComparison.OrdinalIgnoreCase))
            {
                return (i + 1, i + 1, line);
            }

            var lineNormalized = line.TrimStart('#').Trim();
            if (lineNormalized.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return (i + 1, i + 1, line);
            }
        }

        var structure = MarkdownParser.Parse(lines.ToArray());
        var similar = structure.Headings
            .Where(h => h.Text.Contains(normalized.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .Select(h => $"'{new string('#', h.Level)} {h.Text}' (line {h.Line})");

        throw new InvalidOperationException(
            $"Heading '{heading}' not found. Similar: {string.Join(", ", similar)}. Use TextInspect to list all headings.");
    }

    private static int FindHeadingIndex(MarkdownStructure structure, string heading)
    {
        var normalized = heading.TrimStart('#').Trim();

        for (var i = 0; i < structure.Headings.Count; i++)
        {
            var h = structure.Headings[i];

            var fullHeading = $"{new string('#', h.Level)} {h.Text}";
            if (fullHeading.Equals(heading, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }

            if (h.Text.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        var similar = structure.Headings
            .Where(h => h.Text.Contains(normalized.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .Select(h => $"'{new string('#', h.Level)} {h.Text}' (line {h.Line})");

        throw new InvalidOperationException(
            $"Heading '{heading}' not found. Similar: {string.Join(", ", similar)}. Use TextInspect to list all headings.");
    }

    private static (int, int, string?) FindCodeBlockTarget(List<string> lines, int index)
    {
        var structure = MarkdownParser.Parse(lines.ToArray());

        if (index < 0 || index >= structure.CodeBlocks.Count)
        {
            throw new InvalidOperationException(
                $"Code block index {index} out of range. File has {structure.CodeBlocks.Count} code blocks.");
        }

        var block = structure.CodeBlocks[index];
        return (block.StartLine + 1, block.EndLine - 1, null);
    }

    private static JsonObject ApplyReplace(List<string> lines, int startLine, int endLine, string? matchedText,
        string content, bool preserveIndent)
    {
        if (matchedText is not null)
        {
            var lineIndex = startLine - 1;
            var line = lines[lineIndex];
            var newLine = line.Replace(matchedText, content);
            lines[lineIndex] = newLine;

            return new JsonObject
            {
                ["linesChanged"] = 1,
                ["newContent"] = newLine.Length > 200 ? newLine[..200] + "..." : newLine
            };
        }

        var indent = preserveIndent ? GetIndent(lines[startLine - 1]) : "";
        var newLines = content.Split('\n').Select(l => indent + l.TrimStart()).ToList();

        lines.RemoveRange(startLine - 1, endLine - startLine + 1);
        lines.InsertRange(startLine - 1, newLines);

        return new JsonObject
        {
            ["linesChanged"] = endLine - startLine + 1,
            ["newContent"] = content.Length > 200 ? content[..200] + "..." : content
        };
    }

    private static JsonObject ApplyInsert(List<string> lines, JsonObject target, int insertAfterLine, string content,
        bool preserveIndent)
    {
        var indent = "";
        if (preserveIndent && insertAfterLine > 0 && insertAfterLine <= lines.Count)
        {
            indent = GetIndent(lines[insertAfterLine - 1]);
        }

        var newLines = content.Split('\n').Select(l => indent + l.TrimStart()).ToList();

        if (target.ContainsKey("beforeHeading"))
        {
            lines.InsertRange(insertAfterLine, newLines);
        }
        else
        {
            lines.InsertRange(insertAfterLine, newLines);
        }

        return new JsonObject
        {
            ["linesChanged"] = 0,
            ["linesInserted"] = newLines.Count,
            ["newContent"] = content.Length > 200 ? content[..200] + "..." : content
        };
    }

    private static JsonObject ApplyDelete(List<string> lines, int startLine, int endLine)
    {
        var deletedContent = string.Join("\n", lines.Skip(startLine - 1).Take(endLine - startLine + 1));
        lines.RemoveRange(startLine - 1, endLine - startLine + 1);

        return new JsonObject
        {
            ["linesDeleted"] = endLine - startLine + 1,
            ["deletedContent"] = deletedContent.Length > 200 ? deletedContent[..200] + "..." : deletedContent
        };
    }

    private static string GetIndent(string line)
    {
        var indent = 0;
        foreach (var c in line)
        {
            if (c == ' ' || c == '\t')
            {
                indent++;
            }
            else
            {
                break;
            }
        }

        return line[..indent];
    }

    // --- Text replace helpers (from TextReplaceTool) ---

    private static List<int> FindAllOccurrences(string content, string searchText)
    {
        var positions = new List<int>();
        var index = 0;

        while ((index = content.IndexOf(searchText, index, StringComparison.Ordinal)) >= 0)
        {
            positions.Add(index);
            index += searchText.Length;
        }

        return positions;
    }

    private static string? FindCaseInsensitiveSuggestion(string content, string searchText)
    {
        var index = content.IndexOf(searchText, StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? content.Substring(index, searchText.Length) : null;
    }

    private static (string ReplacedContent, int ReplacedCount, int ReplacementPosition) ApplyTextReplacement(
        string content, string oldText, string newText, string occurrence, List<int> positions)
    {
        var occurrenceParam = occurrence.ToLowerInvariant();

        if (occurrenceParam == "all")
        {
            var replaced = content.Replace(oldText, newText);
            return (replaced, positions.Count, positions[0]);
        }

        if (occurrenceParam == "last")
        {
            var position = positions[^1];
            var replaced = content[..position] + newText + content[(position + oldText.Length)..];
            return (replaced, 1, position);
        }

        if (int.TryParse(occurrenceParam, out var nth))
        {
            if (nth < 1 || nth > positions.Count)
            {
                throw new InvalidOperationException(
                    $"Occurrence {nth} requested but only {positions.Count} found");
            }

            var position = positions[nth - 1];
            var replaced = content[..position] + newText + content[(position + oldText.Length)..];
            return (replaced, 1, position);
        }

        var firstPosition = positions[0];
        var replacedFirst = content[..firstPosition] + newText + content[(firstPosition + oldText.Length)..];
        return (replacedFirst, 1, firstPosition);
    }

    private static (int StartLine, int EndLine) ComputeAffectedLines(string content, int position, int oldLength)
    {
        var startLine = content[..position].Count(c => c == '\n') + 1;
        var oldTextContent = content.Substring(position, oldLength);
        var linesInOld = oldTextContent.Count(c => c == '\n');
        var endLine = startLine + linesInOld;
        return (startLine, endLine);
    }

    private static List<string> GetTextReplaceContextLines(string[] lines, int startLine, int endLine)
    {
        const int contextSize = 3;

        var contextStart = Math.Max(0, startLine - 1 - contextSize);
        var contextEnd = Math.Min(lines.Length - 1, endLine - 1 + contextSize);

        var context = new List<string>();
        for (var i = contextStart; i <= contextEnd; i++)
        {
            var lineNum = i + 1;
            var marker = lineNum >= startLine && lineNum <= endLine ? ">" : " ";
            context.Add($"{marker} {lineNum}: {lines[i]}");
        }

        return context;
    }
}
```

**Dependencies**: `Domain/Tools/Text/TextToolBase.cs` (existing, no changes), `Domain/Tools/Text/MarkdownParser.cs` (existing, no changes)
**Provides**: `TextEditTool` class with `Name`, `Description`, `Run(string filePath, string operation, JsonObject target, string? content, string? occurrence, bool preserveIndent, string? expectedHash)` method

---

### Domain/Tools/Text/TextInspectTool.cs [edit]

**Purpose**: Strip to structure-only mode, removing search and lines modes.

**TOTAL CHANGES**: 2

**Changes**:
1. Lines 10-29: Replace Description const to remove mode/search/lines references
2. Lines 31-46: Simplify `Run` method to single parameter, remove mode dispatch

**Implementation Details**:
- Remove `mode`, `query`, `regex`, `context` parameters from `Run`
- Remove `InspectSearch` method (lines 156-238)
- Remove `InspectLines` method (lines 240-263)
- Remove `ParseLineRanges` method (lines 265-283)
- Keep `InspectStructure` method (lines 49-154) and `FormatFileSize` method (lines 286-294)
- Update Description to reference TextSearch for search and TextRead for line reading

**Migration Pattern**:
```csharp
// BEFORE (line 31):
protected JsonNode Run(string filePath, string mode = "structure", string? query = null, bool regex = false,
    int context = 0)

// AFTER:
protected JsonNode Run(string filePath)
```

**Reference Implementation**:
```csharp
using System.Text.Json.Nodes;

namespace Domain.Tools.Text;

public class TextInspectTool(string vaultPath, string[] allowedExtensions) : TextToolBase(vaultPath, allowedExtensions)
{
    protected const string Name = "TextInspect";

    protected const string Description = """
                                         Returns the structure of a text or markdown file without loading full content.

                                         Returns:
                                         - Markdown files: headings (level, text, line), code blocks (startLine, endLine, language), anchors, frontmatter, totalLines, fileSize, fileHash
                                         - Plain text files: sections (INI-style markers), blank line groups, totalLines, fileSize, fileHash

                                         Use this before TextEdit to find exact line numbers and heading names for targeting.
                                         To search within files, use TextSearch with filePath parameter.
                                         To read specific line ranges, use TextRead with a lines target.
                                         """;

    protected JsonNode Run(string filePath)
    {
        var fullPath = ValidateAndResolvePath(filePath);
        var lines = File.ReadAllLines(fullPath);
        var isMarkdown = Path.GetExtension(fullPath).ToLowerInvariant() is ".md" or ".markdown";

        var result = new JsonObject
        {
            ["filePath"] = fullPath,
            ["totalLines"] = lines.Length,
            ["fileSize"] = FormatFileSize(new FileInfo(fullPath).Length),
            ["format"] = isMarkdown ? "markdown" : "text"
        };

        if (isMarkdown)
        {
            var structure = MarkdownParser.Parse(lines);
            var structureNode = new JsonObject();

            if (structure.Frontmatter is not null)
            {
                structureNode["frontmatter"] = new JsonObject
                {
                    ["startLine"] = structure.Frontmatter.StartLine,
                    ["endLine"] = structure.Frontmatter.EndLine,
                    ["keys"] = new JsonArray(structure.Frontmatter.Keys.Select(k => JsonValue.Create(k)).ToArray())
                };
            }

            var headingsArray = new JsonArray();
            foreach (var h in structure.Headings)
            {
                headingsArray.Add(new JsonObject
                {
                    ["level"] = h.Level,
                    ["text"] = h.Text,
                    ["line"] = h.Line
                });
            }

            structureNode["headings"] = headingsArray;

            var codeBlocksArray = new JsonArray();
            foreach (var cb in structure.CodeBlocks)
            {
                var cbNode = new JsonObject
                {
                    ["startLine"] = cb.StartLine,
                    ["endLine"] = cb.EndLine
                };
                if (cb.Language is not null)
                {
                    cbNode["language"] = cb.Language;
                }

                codeBlocksArray.Add(cbNode);
            }

            structureNode["codeBlocks"] = codeBlocksArray;

            if (structure.Anchors.Count > 0)
            {
                var anchorsArray = new JsonArray();
                foreach (var a in structure.Anchors)
                {
                    anchorsArray.Add(new JsonObject
                    {
                        ["id"] = a.Id,
                        ["line"] = a.Line
                    });
                }

                structureNode["anchors"] = anchorsArray;
            }

            result["structure"] = structureNode;
        }
        else
        {
            var structure = MarkdownParser.ParsePlainText(lines);
            var structureNode = new JsonObject();

            if (structure.Sections.Count > 0)
            {
                var sectionsArray = new JsonArray();
                foreach (var s in structure.Sections)
                {
                    sectionsArray.Add(new JsonObject
                    {
                        ["marker"] = s.Marker,
                        ["line"] = s.Line
                    });
                }

                structureNode["sections"] = sectionsArray;
            }

            if (structure.BlankLineGroups.Count > 0)
            {
                structureNode["blankLineGroups"] = new JsonArray(
                    structure.BlankLineGroups.Select(b => JsonValue.Create(b)).ToArray());
            }

            result["structure"] = structureNode;
        }

        result["fileHash"] = ComputeFileHash(lines);

        return result;
    }

    private static string FormatFileSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes}B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
            _ => $"{bytes / (1024.0 * 1024.0):F1}MB"
        };
    }
}
```

**Dependencies**: `Domain/Tools/Text/TextToolBase.cs` (existing), `Domain/Tools/Text/MarkdownParser.cs` (existing)
**Provides**: Simplified `TextInspectTool.Run(string filePath)` method

---

### Domain/Tools/Text/TextSearchTool.cs [edit]

**Purpose**: Add single-file search capability and rename `path` to `directoryPath`.

**TOTAL CHANGES**: 3

**Changes**:
1. Lines 9-29: Update Description to include filePath parameter and cross-reference TextEdit
2. Lines 42-48: Add `filePath` parameter to `Run` method, rename `path` to `directoryPath`, add single-file dispatch
3. Lines 188-198: Rename `ResolvePath` parameter references

**Implementation Details**:
- Add `string? filePath = null` as first optional parameter after `query`
- Rename `path` parameter to `directoryPath` in `Run` signature
- When `filePath` is provided: resolve and validate the path, search only that file, return results in same format
- Add `ValidateAndResolveSingleFilePath(string filePath)` private method that checks path is within vault and has allowed extension
- The `SearchSingleFile` method already exists and can be reused

**Migration Pattern**:
```csharp
// BEFORE (line 42):
protected JsonNode Run(
    string query,
    bool regex = false,
    string? filePattern = null,
    string path = "/",
    int maxResults = 50,
    int contextLines = 1)

// AFTER:
protected JsonNode Run(
    string query,
    bool regex = false,
    string? filePath = null,
    string? filePattern = null,
    string directoryPath = "/",
    int maxResults = 50,
    int contextLines = 1)
```

**Reference Implementation**:
```csharp
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Domain.Tools.Text;

public class TextSearchTool(string vaultPath, string[] allowedExtensions)
{
    protected const string Name = "TextSearch";

    protected const string Description = """
                                         Searches for text across files in the vault, or within a single file.

                                         Returns matching files with line numbers and context.
                                         To modify matching content, use TextEdit with a text target.

                                         Parameters:
                                         - query: Text or regex pattern to search for
                                         - regex: Treat query as regex pattern (default: false)
                                         - filePath: Optional. Search within this single file only (ignores directoryPath and filePattern)
                                         - filePattern: Glob pattern to filter files (e.g., "*.md")
                                         - directoryPath: Directory to search in (default: "/" for entire vault)
                                         - maxResults: Maximum number of matches to return (default: 50)
                                         - contextLines: Lines of context around each match (default: 1)

                                         Examples:
                                         - Find all mentions of "kubernetes": query="kubernetes"
                                         - Find in single file: query="config", filePath="docs/setup.md"
                                         - Find TODOs: query="TODO:.*", regex=true
                                         - Search only in docs: query="api", directoryPath="/docs"
                                         - Search markdown files: query="config", filePattern="*.md"
                                         """;

    private record SearchParams(string Query, Regex? Pattern, int ContextLines, int MaxResults);

    private record FileMatch(string File, IReadOnlyList<MatchResult> Matches);

    private record MatchResult(
        int LineNumber,
        string Text,
        string? Section,
        IReadOnlyList<string>? ContextBefore,
        IReadOnlyList<string>? ContextAfter);

    protected JsonNode Run(
        string query,
        bool regex = false,
        string? filePath = null,
        string? filePattern = null,
        string directoryPath = "/",
        int maxResults = 50,
        int contextLines = 1)
    {
        var searchParams = new SearchParams(
            query,
            regex ? new Regex(query, RegexOptions.IgnoreCase) : null,
            contextLines,
            maxResults);

        if (filePath is not null)
        {
            return RunSingleFileSearch(filePath, query, regex, searchParams);
        }

        var fullPath = ResolvePath(directoryPath);

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Directory not found: {directoryPath}");
        }

        var files = EnumerateAllowedFiles(fullPath, filePattern);
        var (results, filesSearched) = SearchFiles(files, searchParams);
        var totalMatches = results.Sum(r => r.Matches.Count);

        return BuildResultJson(query, regex, directoryPath, filesSearched, results, totalMatches, maxResults);
    }

    private JsonNode RunSingleFileSearch(string filePath, string query, bool regex, SearchParams searchParams)
    {
        var fullPath = ValidateAndResolveSingleFilePath(filePath);

        var matches = SearchSingleFile(fullPath, searchParams, searchParams.MaxResults);
        var results = matches.Count > 0
            ? [new FileMatch(ToRelativePath(fullPath), matches)]
            : new List<FileMatch>();

        return BuildResultJson(query, regex, filePath, 1, results, matches.Count, searchParams.MaxResults);
    }

    private string ValidateAndResolveSingleFilePath(string filePath)
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

        if (!IsAllowedExtension(fullPath))
        {
            var ext = Path.GetExtension(fullPath).ToLowerInvariant();
            throw new InvalidOperationException(
                $"File type '{ext}' not allowed. Allowed: {string.Join(", ", allowedExtensions)}");
        }

        return fullPath;
    }

    private IEnumerable<string> EnumerateAllowedFiles(string fullPath, string? filePattern)
    {
        return Directory
            .EnumerateFiles(fullPath, filePattern ?? "*", SearchOption.AllDirectories)
            .Where(IsAllowedExtension);
    }

    private bool IsAllowedExtension(string filePath)
    {
        return allowedExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant());
    }

    private (List<FileMatch> Results, int FilesSearched) SearchFiles(
        IEnumerable<string> files, SearchParams searchParams)
    {
        var results = new List<FileMatch>();
        var filesSearched = 0;
        var totalMatches = 0;

        foreach (var file in files)
        {
            filesSearched++;
            var remaining = searchParams.MaxResults - totalMatches;
            if (remaining <= 0)
            {
                break;
            }

            var matches = SearchSingleFile(file, searchParams, remaining);
            if (matches.Count == 0)
            {
                continue;
            }

            results.Add(new FileMatch(ToRelativePath(file), matches));
            totalMatches += matches.Count;
        }

        return (results, filesSearched);
    }

    private IReadOnlyList<MatchResult> SearchSingleFile(string filePath, SearchParams searchParams, int maxMatches)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);
            return FindMatchesInLines(lines, searchParams, maxMatches).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<MatchResult> FindMatchesInLines(
        string[] lines, SearchParams searchParams, int maxMatches)
    {
        return lines
            .Select((text, index) => (Text: text, Index: index))
            .Where(line => IsMatchingLine(line.Text, searchParams))
            .Take(maxMatches)
            .Select(line => CreateMatchResult(lines, line.Index, searchParams.ContextLines));
    }

    private static bool IsMatchingLine(string line, SearchParams searchParams)
    {
        return searchParams.Pattern?.IsMatch(line) ??
               line.Contains(searchParams.Query, StringComparison.OrdinalIgnoreCase);
    }

    private static MatchResult CreateMatchResult(string[] lines, int index, int contextLines)
    {
        return new MatchResult(
            LineNumber: index + 1,
            Text: Truncate(lines[index], 200),
            Section: FindNearestHeading(lines, index),
            ContextBefore: contextLines > 0 ? GetContextBefore(lines, index, contextLines) : null,
            ContextAfter: contextLines > 0 ? GetContextAfter(lines, index, contextLines) : null);
    }

    private static IReadOnlyList<string> GetContextBefore(string[] lines, int index, int count)
    {
        return lines
            .Take(index)
            .TakeLast(count)
            .Select(l => Truncate(l, 100))
            .ToList();
    }

    private static IReadOnlyList<string> GetContextAfter(string[] lines, int index, int count)
    {
        return lines
            .Skip(index + 1)
            .Take(count)
            .Select(l => Truncate(l, 100))
            .ToList();
    }

    private static string? FindNearestHeading(string[] lines, int lineIndex)
    {
        return lines
            .Take(lineIndex + 1)
            .Reverse()
            .FirstOrDefault(l => l.StartsWith('#'))
            ?.TrimStart('#')
            .Trim();
    }

    private static string Truncate(string text, int maxLength)
    {
        return text.Length > maxLength ? text[..maxLength] + "..." : text;
    }

    private string ToRelativePath(string fullPath)
    {
        return Path.GetRelativePath(vaultPath, fullPath).Replace('\\', '/');
    }

    private string ResolvePath(string path)
    {
        var normalized = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var fullPath = string.IsNullOrEmpty(normalized)
            ? vaultPath
            : Path.GetFullPath(Path.Combine(vaultPath, normalized));

        return fullPath.StartsWith(vaultPath, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : throw new UnauthorizedAccessException("Access denied: path must be within vault directory");
    }

    private JsonNode BuildResultJson(
        string query,
        bool regex,
        string path,
        int filesSearched,
        List<FileMatch> results,
        int totalMatches,
        int maxResults)
    {
        return new JsonObject
        {
            ["query"] = query,
            ["regex"] = regex,
            ["path"] = path,
            ["filesSearched"] = filesSearched,
            ["filesWithMatches"] = results.Count,
            ["totalMatches"] = totalMatches,
            ["truncated"] = totalMatches >= maxResults,
            ["results"] = new JsonArray(results.Select(ToFileMatchJson).ToArray())
        };
    }

    private static JsonNode ToFileMatchJson(FileMatch fileMatch)
    {
        return new JsonObject
        {
            ["file"] = fileMatch.File,
            ["matches"] = new JsonArray(fileMatch.Matches.Select(ToMatchResultJson).ToArray())
        };
    }

    private static JsonNode ToMatchResultJson(MatchResult match)
    {
        var obj = new JsonObject
        {
            ["line"] = match.LineNumber,
            ["text"] = match.Text
        };

        if (match.Section is not null)
        {
            obj["section"] = match.Section;
        }

        if (match.ContextBefore?.Count > 0 || match.ContextAfter?.Count > 0)
        {
            obj["context"] = new JsonObject
            {
                ["before"] = ToJsonArray(match.ContextBefore ?? []),
                ["after"] = ToJsonArray(match.ContextAfter ?? [])
            };
        }

        return obj;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> items)
    {
        return new JsonArray(items.Select(s => JsonValue.Create(s)).ToArray<JsonNode>());
    }
}
```

**Dependencies**: None (standalone class)
**Provides**: Updated `TextSearchTool.Run(string query, bool regex, string? filePath, string? filePattern, string directoryPath, int maxResults, int contextLines)` method

---

### Domain/Tools/Text/TextReadTool.cs [edit]

**Purpose**: Update description to reference TextInspect (not TextInspect search mode) and TextSearch.

**TOTAL CHANGES**: 1

**Changes**:
1. Lines 9-30: Update Description const to remove reference to "TextInspect with search mode" and add reference to TextSearch

**Migration Pattern**:
```csharp
// BEFORE (line 24):
5. To search within a file, use TextInspect with search mode

// AFTER:
5. To search within a file, use TextSearch with filePath parameter
```

**Reference Implementation** (only the changed Description const, rest of file unchanged):
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
                                         5. To search within a file, use TextSearch with filePath parameter

                                         Examples:
                                         - Read lines 50-75: target={ "lines": { "start": 50, "end": 75 } }
                                         - Read Installation section: target={ "heading": "## Installation" }
                                         - Read third code block: target={ "codeBlock": { "index": 2 } }
                                         """;
```

**Dependencies**: None
**Provides**: Updated description cross-reference

---

### Domain/Tools/Text/TextCreateTool.cs [edit]

**Purpose**: Update error message to reference TextEdit instead of TextPatch.

**TOTAL CHANGES**: 1

**Changes**:
1. Line 63: Change "Use TextPatch to modify existing files." to "Use TextEdit to modify existing files."

**Migration Pattern**:
```csharp
// BEFORE (line 63):
$"File already exists: {originalPath}. Use TextPatch to modify existing files."

// AFTER:
$"File already exists: {originalPath}. Use TextEdit to modify existing files."
```

**Reference Implementation**: Single line change as shown above.

**Dependencies**: None
**Provides**: Updated error message

---

### Domain/Tools/Files/ListFilesTool.cs [edit]

**Purpose**: Rename `path` parameter to `directoryPath`.

**TOTAL CHANGES**: 2

**Changes**:
1. Line 17: Rename parameter `string path` to `string directoryPath`
2. Line 19: Update `path.StartsWith(...)` to `directoryPath.StartsWith(...)`

**Migration Pattern**:
```csharp
// BEFORE (line 17):
protected async Task<JsonNode> Run(string path, CancellationToken cancellationToken)
{
    if (!path.StartsWith(libraryPath.BaseLibraryPath))

// AFTER:
protected async Task<JsonNode> Run(string directoryPath, CancellationToken cancellationToken)
{
    if (!directoryPath.StartsWith(libraryPath.BaseLibraryPath))
```

**Reference Implementation** (Run method only, rest unchanged):
```csharp
    protected async Task<JsonNode> Run(string directoryPath, CancellationToken cancellationToken)
    {
        if (!directoryPath.StartsWith(libraryPath.BaseLibraryPath))
        {
            throw new InvalidOperationException($"""
                                                 {typeof(ListFilesTool)} parameter must be absolute paths derived from
                                                 the ListDirectories tool response.
                                                 They must start with the library path: {libraryPath}
                                                 """);
        }

        var result = await client.ListFilesIn(directoryPath, cancellationToken);
        return JsonSerializer.SerializeToNode(result) ??
               throw new InvalidOperationException("Failed to serialize ListFiles");
    }
```

**Dependencies**: None
**Provides**: Renamed `directoryPath` parameter

---

### Domain/Tools/Files/RemoveFileTool.cs [edit]

**Purpose**: Rename `path` parameter to `filePath`.

**TOTAL CHANGES**: 2

**Changes**:
1. Line 16: Rename parameter `string path` to `string filePath`
2. Lines 17-27: Update all usages of `path` to `filePath`

**Migration Pattern**:
```csharp
// BEFORE (line 16):
protected async Task<JsonNode> Run(string path, CancellationToken cancellationToken)

// AFTER:
protected async Task<JsonNode> Run(string filePath, CancellationToken cancellationToken)
```

**Reference Implementation** (Run method only):
```csharp
    protected async Task<JsonNode> Run(string filePath, CancellationToken cancellationToken)
    {
        ValidatePathWithinLibrary(filePath);

        var trashPath = await client.MoveToTrash(filePath, cancellationToken);
        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "File moved to trash",
            ["originalPath"] = filePath,
            ["trashPath"] = trashPath
        };
    }

    private void ValidatePathWithinLibrary(string filePath)
    {
        if (filePath.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{nameof(RemoveFileTool)} path must not contain '..' segments.");
        }

        var canonicalLibraryPath = Path.GetFullPath(libraryPath.BaseLibraryPath);
        var canonicalFilePath = Path.GetFullPath(filePath);

        if (!canonicalFilePath.StartsWith(canonicalLibraryPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"""
                                                 {nameof(RemoveFileTool)} path must be within the library.
                                                 Resolved path '{canonicalFilePath}' is not under library path '{canonicalLibraryPath}'.
                                                 """);
        }
    }
```

**Dependencies**: None
**Provides**: Renamed `filePath` parameter

---

### McpServerText/McpTools/McpTextEditTool.cs [create]

**Purpose**: MCP wrapper for the new TextEditTool, exposing it via Model Context Protocol.

**TOTAL CHANGES**: 1 (new file creation)

**Changes**:
1. Create new MCP tool class that inherits TextEditTool

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
public class McpTextEditTool(McpSettings settings)
    : TextEditTool(settings.VaultPath, settings.AllowedExtensions)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public CallToolResult McpRun(
        [Description("Path to the text file (absolute or relative to vault)")]
        string filePath,
        [Description("Operation: 'replace', 'insert', or 'delete'. Text targets only support 'replace'.")]
        string operation,
        [Description(
            "Target specification as JSON. Use ONE of: lines {start,end}, heading, beforeHeading, appendToSection, codeBlock {index}, text \"exact match\"")]
        string target,
        [Description("New content for replace/insert operations. For text targets, this is the replacement text.")]
        string? content = null,
        [Description(
            "For text targets only: which occurrence to replace. 'first' (default), 'last', 'all', or numeric 1-based index.")]
        string? occurrence = null,
        [Description("Match indentation of target line (default: true). Only applies to positional targets.")]
        bool preserveIndent = true,
        [Description("Expected file hash for staleness detection. Get from TextInspect.")]
        string? expectedHash = null)
    {
        var targetObj = JsonNode.Parse(target)?.AsObject() ??
                        throw new ArgumentException("Target must be a valid JSON object");

        return ToolResponse.Create(Run(filePath, operation, targetObj, content, occurrence, preserveIndent,
            expectedHash));
    }
}
```

**Dependencies**: `Domain/Tools/Text/TextEditTool.cs` (new), `Infrastructure/Utils/ToolResponse.cs` (existing)
**Provides**: `McpTextEditTool` class for MCP registration

---

### McpServerText/McpTools/McpTextInspectTool.cs [edit]

**Purpose**: Simplify to single `filePath` parameter matching stripped TextInspectTool.

**TOTAL CHANGES**: 1

**Changes**:
1. Lines 16-29: Replace McpRun method to remove mode, query, regex, context params

**Reference Implementation**:
```csharp
using System.ComponentModel;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerText.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class McpTextInspectTool(McpSettings settings)
    : TextInspectTool(settings.VaultPath, settings.AllowedExtensions)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public CallToolResult McpRun(
        [Description("Path to the text file (absolute or relative to vault)")]
        string filePath)
    {
        return ToolResponse.Create(Run(filePath));
    }
}
```

**Dependencies**: `Domain/Tools/Text/TextInspectTool.cs` (edited)
**Provides**: Simplified `McpTextInspectTool.McpRun(string filePath)`

---

### McpServerText/McpTools/McpTextSearchTool.cs [edit]

**Purpose**: Add filePath parameter and rename path to directoryPath.

**TOTAL CHANGES**: 1

**Changes**:
1. Lines 16-31: Update McpRun to add filePath param, rename path to directoryPath

**Reference Implementation**:
```csharp
using System.ComponentModel;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerText.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class McpTextSearchTool(McpSettings settings)
    : TextSearchTool(settings.VaultPath, settings.AllowedExtensions)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public CallToolResult McpRun(
        [Description("Text or regex pattern to search for")]
        string query,
        [Description("Treat query as regex pattern")]
        bool regex = false,
        [Description("Search within this single file only. When set, directoryPath and filePattern are ignored.")]
        string? filePath = null,
        [Description("Glob pattern to filter files (e.g., '*.md')")]
        string? filePattern = null,
        [Description("Directory to search in (default: entire vault)")]
        string directoryPath = "/",
        [Description("Maximum number of matches to return")]
        int maxResults = 50,
        [Description("Lines of context around each match")]
        int contextLines = 1)
    {
        return ToolResponse.Create(Run(query, regex, filePath, filePattern, directoryPath, maxResults, contextLines));
    }
}
```

**Dependencies**: `Domain/Tools/Text/TextSearchTool.cs` (edited)
**Provides**: Updated `McpTextSearchTool.McpRun` with filePath and directoryPath

---

### McpServerText/McpTools/McpTextReadTool.cs [edit]

**Purpose**: No parameter changes needed. Description is inherited from domain. File unchanged.

**TOTAL CHANGES**: 0

This file requires no changes -- the Description const is inherited from TextReadTool which is being updated separately. Including here for completeness since the design mentioned it, but no edits are needed.

**Dependencies**: `Domain/Tools/Text/TextReadTool.cs` (edited -- description propagates via inheritance)
**Provides**: Updated description via inheritance

---

### McpServerText/McpTools/McpTextListFilesTool.cs [edit]

**Purpose**: Rename `path` parameter to `directoryPath`.

**TOTAL CHANGES**: 1

**Changes**:
1. Lines 18-24: Rename `path` to `directoryPath` in McpRun

**Reference Implementation**:
```csharp
using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class McpTextListFilesTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath) : ListFilesTool(client, libraryPath)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        [Description("Absolute path to the directory")]
        string directoryPath,
        CancellationToken cancellationToken)
    {
        return ToolResponse.Create(await Run(directoryPath, cancellationToken));
    }
}
```

**Dependencies**: `Domain/Tools/Files/ListFilesTool.cs` (edited)
**Provides**: Renamed parameter in MCP wrapper

---

### McpServerText/McpTools/McpRemoveFileTool.cs [edit]

**Purpose**: Rename `path` parameter to `filePath`.

**TOTAL CHANGES**: 1

**Changes**:
1. Lines 18-24: Rename `path` to `filePath` in McpRun

**Reference Implementation**:
```csharp
using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class McpRemoveFileTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath) : RemoveFileTool(client, libraryPath)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        [Description("Absolute path to the file (from ListFiles)")]
        string filePath,
        CancellationToken cancellationToken)
    {
        return ToolResponse.Create(await Run(filePath, cancellationToken));
    }
}
```

**Dependencies**: `Domain/Tools/Files/RemoveFileTool.cs` (edited)
**Provides**: Renamed parameter in MCP wrapper

---

### McpServerText/Modules/ConfigModule.cs [edit]

**Purpose**: Replace McpTextPatchTool registration with McpTextEditTool, remove any TextReplace references.

**TOTAL CHANGES**: 1

**Changes**:
1. Line 58: Replace `.WithTools<McpTextPatchTool>()` with `.WithTools<McpTextEditTool>()`

**Migration Pattern**:
```csharp
// BEFORE (line 58):
.WithTools<McpTextPatchTool>()

// AFTER:
.WithTools<McpTextEditTool>()
```

**Reference Implementation** (relevant section of ConfigureMcp method):
```csharp
            // Text tools
            .WithTools<McpTextSearchTool>()
            .WithTools<McpTextInspectTool>()
            .WithTools<McpTextReadTool>()
            .WithTools<McpTextEditTool>()
            .WithTools<McpTextCreateTool>()
```

**Dependencies**: `McpServerText/McpTools/McpTextEditTool.cs` (new)
**Provides**: Updated tool registration

---

### Tests/Unit/Domain/Text/TextEditToolTests.cs [create]

**Purpose**: Comprehensive tests for the merged TextEditTool, covering both positional and text-match targeting. Combines all tests from TextPatchToolTests and TextReplaceToolTests.

**TOTAL CHANGES**: 1 (new file creation)

**Changes**:
1. Create test class with all tests from both TextPatchToolTests and TextReplaceToolTests, adapted for TextEditTool API

**Implementation Details**:
- Testable wrapper: `TestableTextEditTool` that exposes protected `Run` method
- Positional tests use `target` with positional keys (lines, heading, beforeHeading, appendToSection, codeBlock)
- Text-match tests use `target` with `text` key and pass `occurrence` param
- New tests for: text target with non-replace operation throws, invalid target error message includes "text"

**Test File**: This IS the test file.

**Test cases**:
- Test: `Run_InsertBeforeHeading_InsertsContent` -- Asserts: content inserted before heading, status success
- Test: `Run_DeleteLines_RemovesLines` -- Asserts: lines 2-4 removed, linesDelta is -3
- Test: `Run_ReplaceCodeBlock_ReplacesContent` -- Asserts: code block content replaced, fences preserved
- Test: `Run_HeadingNotFound_ThrowsWithSimilar` -- Asserts: InvalidOperationException with "not found"
- Test: `Run_MissingContentForReplace_ThrowsException` -- Asserts: ArgumentException for missing content
- Test: `Run_PathOutsideVault_ThrowsException` -- Asserts: UnauthorizedAccessException
- Test: `Run_AppendToSection_InsertsAtEndOfSection` -- Asserts: content between section text and next heading
- Test: `Run_AppendToSection_LastSection_InsertsAtEndOfFile` -- Asserts: content at end of file
- Test: `Run_AppendToSection_NonMarkdown_Throws` -- Asserts: InvalidOperationException with "markdown"
- Test: `Run_AppendToSection_HeadingNotFound_Throws` -- Asserts: InvalidOperationException
- Test: `Run_InvalidOperation_ThrowsException` -- Asserts: ArgumentException for "replaceLines"
- Test: `Run_TextTarget_SingleOccurrence_ReplacesText` -- Asserts: status success, occurrencesFound 1, file content changed
- Test: `Run_TextTarget_MultipleOccurrences_ReplacesFirst_ByDefault` -- Asserts: only first replaced, note about remaining
- Test: `Run_TextTarget_ReplacesLast` -- Asserts: last occurrence replaced
- Test: `Run_TextTarget_ReplacesAll` -- Asserts: all occurrences replaced
- Test: `Run_TextTarget_ReplacesNth` -- Asserts: 2nd occurrence replaced
- Test: `Run_TextTarget_NthExceedsTotal_Throws` -- Asserts: InvalidOperationException
- Test: `Run_TextTarget_NotFound_ThrowsWithMessage` -- Asserts: InvalidOperationException with "not found"
- Test: `Run_TextTarget_CaseInsensitiveMatch_ThrowsWithSuggestion` -- Asserts: "Did you mean"
- Test: `Run_TextTarget_MultilineOldText_ReplacesAcrossLines` -- Asserts: multiline replaced
- Test: `Run_TextTarget_ReturnsContextLines` -- Asserts: context array has content
- Test: `Run_TextTarget_ReturnsFileHash` -- Asserts: 16-char hex hash
- Test: `Run_TextTarget_ExpectedHashMatches_Succeeds` -- Asserts: status success with matching hash
- Test: `Run_TextTarget_ExpectedHashMismatches_Throws` -- Asserts: InvalidOperationException "hash mismatch"
- Test: `Run_TextTarget_InsertOperation_Throws` -- Asserts: ArgumentException "only supports 'replace'"
- Test: `Run_TextTarget_DeleteOperation_Throws` -- Asserts: ArgumentException "only supports 'replace'"
- Test: `Run_TextTarget_DisallowedExtension_Throws` -- Asserts: InvalidOperationException
- Test: `Run_InvalidTarget_ThrowsWithTextInList` -- Asserts: error message includes "text" in valid target list

**Reference Implementation**:
```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Domain.Tools.Text;
using Shouldly;

namespace Tests.Unit.Domain.Text;

public class TextEditToolTests : IDisposable
{
    private readonly string _testDir;
    private readonly TestableTextEditTool _tool;

    public TextEditToolTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"text-edit-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
        _tool = new TestableTextEditTool(_testDir, [".md", ".txt"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    // --- Positional targeting tests (from TextPatchToolTests) ---

    [Fact]
    public void Run_InsertBeforeHeading_InsertsContent()
    {
        var content = "# Introduction\nIntro text\n## Setup\nSetup text";
        var filePath = CreateTestFile("doc.md", content);

        var target = new JsonObject { ["beforeHeading"] = "## Setup" };
        var result = _tool.TestRun(filePath, "insert", target, "## New Section\nNew content\n");

        result["status"]!.ToString().ShouldBe("success");
        var newContent = File.ReadAllText(filePath);
        newContent.ShouldContain("New Section");
        newContent.ShouldContain("New content");
    }

    [Fact]
    public void Run_DeleteLines_RemovesLines()
    {
        var content = "Line 1\nLine 2\nLine 3\nLine 4\nLine 5";
        var filePath = CreateTestFile("test.md", content);

        var target = new JsonObject { ["lines"] = new JsonObject { ["start"] = 2, ["end"] = 4 } };
        var result = _tool.TestRun(filePath, "delete", target);

        result["status"]!.ToString().ShouldBe("success");
        result["linesDelta"]!.GetValue<int>().ShouldBe(-3);
        var newContent = File.ReadAllText(filePath);
        newContent.ShouldContain("Line 1");
        newContent.ShouldContain("Line 5");
        newContent.ShouldNotContain("Line 2");
        newContent.ShouldNotContain("Line 3");
        newContent.ShouldNotContain("Line 4");
    }

    [Fact]
    public void Run_ReplaceCodeBlock_ReplacesContent()
    {
        var content = "# Code\n```csharp\nold code\n```\nText";
        var filePath = CreateTestFile("code.md", content);

        var target = new JsonObject { ["codeBlock"] = new JsonObject { ["index"] = 0 } };
        var result = _tool.TestRun(filePath, "replace", target, "new code here");

        result["status"]!.ToString().ShouldBe("success");
        var newContent = File.ReadAllText(filePath);
        newContent.ShouldContain("new code here");
        newContent.ShouldNotContain("old code");
        newContent.ShouldContain("```csharp");
    }

    [Fact]
    public void Run_HeadingNotFound_ThrowsWithSimilar()
    {
        var content = "# Introduction\n## Installation\n## Configuration";
        var filePath = CreateTestFile("doc.md", content);

        var target = new JsonObject { ["heading"] = "## Instalation" };

        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "replace", target, "## Setup"));
        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public void Run_MissingContentForReplace_ThrowsException()
    {
        var filePath = CreateTestFile("test.md", "Content");

        var target = new JsonObject { ["lines"] = new JsonObject { ["start"] = 1, ["end"] = 1 } };

        Should.Throw<ArgumentException>(() => _tool.TestRun(filePath, "replace", target));
    }

    [Fact]
    public void Run_PathOutsideVault_ThrowsException()
    {
        var target = new JsonObject { ["lines"] = new JsonObject { ["start"] = 1, ["end"] = 1 } };

        Should.Throw<UnauthorizedAccessException>(() =>
            _tool.TestRun("/etc/passwd", "replace", target, "new"));
    }

    [Fact]
    public void Run_AppendToSection_InsertsAtEndOfSection()
    {
        var content = "# Intro\nIntro text\n## Setup\nSetup text\n## Config\nConfig text";
        var filePath = CreateTestFile("doc.md", content);

        var target = new JsonObject { ["appendToSection"] = "## Setup" };
        var result = _tool.TestRun(filePath, "insert", target, "New content");

        result["status"]!.ToString().ShouldBe("success");
        var newContent = File.ReadAllText(filePath);

        newContent.ShouldContain("Setup text");
        newContent.ShouldContain("New content");
        newContent.ShouldContain("## Config");

        var setupTextIndex = newContent.IndexOf("Setup text", StringComparison.Ordinal);
        var newContentIndex = newContent.IndexOf("New content", StringComparison.Ordinal);
        var configIndex = newContent.IndexOf("## Config", StringComparison.Ordinal);

        newContentIndex.ShouldBeGreaterThan(setupTextIndex);
        configIndex.ShouldBeGreaterThan(newContentIndex);
    }

    [Fact]
    public void Run_AppendToSection_LastSection_InsertsAtEndOfFile()
    {
        var content = "# Intro\nIntro text\n## Setup\nSetup text";
        var filePath = CreateTestFile("doc.md", content);

        var target = new JsonObject { ["appendToSection"] = "## Setup" };
        var result = _tool.TestRun(filePath, "insert", target, "New content at end");

        result["status"]!.ToString().ShouldBe("success");
        var newContent = File.ReadAllText(filePath);

        newContent.ShouldContain("New content at end");
        var setupTextIndex = newContent.IndexOf("Setup text", StringComparison.Ordinal);
        var newContentIndex = newContent.IndexOf("New content at end", StringComparison.Ordinal);
        newContentIndex.ShouldBeGreaterThan(setupTextIndex);
    }

    [Fact]
    public void Run_AppendToSection_NonMarkdown_Throws()
    {
        var content = "Some text\nMore text";
        var filePath = CreateTestFile("test.txt", content);

        var target = new JsonObject { ["appendToSection"] = "Section" };

        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "insert", target, "New content"));
        ex.Message.ShouldContain("markdown");
    }

    [Fact]
    public void Run_AppendToSection_HeadingNotFound_Throws()
    {
        var content = "# Intro\nIntro text\n## Setup\nSetup text";
        var filePath = CreateTestFile("doc.md", content);

        var target = new JsonObject { ["appendToSection"] = "## Config" };

        Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "insert", target, "New content"));
    }

    [Fact]
    public void Run_InvalidOperation_ThrowsException()
    {
        var filePath = CreateTestFile("test.md", "Line 1\nLine 2\nLine 3");

        var target = new JsonObject { ["lines"] = new JsonObject { ["start"] = 1, ["end"] = 2 } };

        var ex = Should.Throw<ArgumentException>(() =>
            _tool.TestRun(filePath, "replaceLines", target, "New content"));
        ex.Message.ShouldContain("replaceLines");
    }

    // --- Text target tests (from TextReplaceToolTests) ---

    [Fact]
    public void Run_TextTarget_SingleOccurrence_ReplacesText()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var target = new JsonObject { ["text"] = "World" };
        var result = _tool.TestRun(filePath, "replace", target, "Universe");

        result["status"]!.ToString().ShouldBe("success");
        result["occurrencesFound"]!.GetValue<int>().ShouldBe(1);
        result["occurrencesReplaced"]!.GetValue<int>().ShouldBe(1);
        var content = File.ReadAllText(filePath);
        content.ShouldBe("Hello Universe");
    }

    [Fact]
    public void Run_TextTarget_MultipleOccurrences_ReplacesFirst_ByDefault()
    {
        var filePath = CreateTestFile("test.txt", "foo bar foo baz foo");

        var target = new JsonObject { ["text"] = "foo" };
        var result = _tool.TestRun(filePath, "replace", target, "FOO");

        result["status"]!.ToString().ShouldBe("success");
        result["occurrencesFound"]!.GetValue<int>().ShouldBe(3);
        result["occurrencesReplaced"]!.GetValue<int>().ShouldBe(1);
        var content = File.ReadAllText(filePath);
        content.ShouldBe("FOO bar foo baz foo");
        result["note"]!.ToString().ShouldContain("2 other occurrence(s) remain");
    }

    [Fact]
    public void Run_TextTarget_ReplacesLast()
    {
        var filePath = CreateTestFile("test.txt", "foo bar foo baz foo");

        var target = new JsonObject { ["text"] = "foo" };
        var result = _tool.TestRun(filePath, "replace", target, "FOO", occurrence: "last");

        result["status"]!.ToString().ShouldBe("success");
        var content = File.ReadAllText(filePath);
        content.ShouldBe("foo bar foo baz FOO");
    }

    [Fact]
    public void Run_TextTarget_ReplacesAll()
    {
        var filePath = CreateTestFile("test.txt", "foo bar foo baz foo");

        var target = new JsonObject { ["text"] = "foo" };
        var result = _tool.TestRun(filePath, "replace", target, "FOO", occurrence: "all");

        result["status"]!.ToString().ShouldBe("success");
        result["occurrencesReplaced"]!.GetValue<int>().ShouldBe(3);
        var content = File.ReadAllText(filePath);
        content.ShouldBe("FOO bar FOO baz FOO");
        result.AsObject().ContainsKey("note").ShouldBeFalse();
    }

    [Fact]
    public void Run_TextTarget_ReplacesNth()
    {
        var filePath = CreateTestFile("test.txt", "foo bar foo baz foo");

        var target = new JsonObject { ["text"] = "foo" };
        var result = _tool.TestRun(filePath, "replace", target, "FOO", occurrence: "2");

        result["status"]!.ToString().ShouldBe("success");
        var content = File.ReadAllText(filePath);
        content.ShouldBe("foo bar FOO baz foo");
    }

    [Fact]
    public void Run_TextTarget_NthExceedsTotal_Throws()
    {
        var filePath = CreateTestFile("test.txt", "foo bar");

        var target = new JsonObject { ["text"] = "foo" };

        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "replace", target, "FOO", occurrence: "5"));
        ex.Message.ShouldContain("Occurrence 5 requested but only 1 found");
    }

    [Fact]
    public void Run_TextTarget_NotFound_ThrowsWithMessage()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var target = new JsonObject { ["text"] = "Missing" };

        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "replace", target, "X"));
        ex.Message.ShouldContain("Text 'Missing' not found");
    }

    [Fact]
    public void Run_TextTarget_CaseInsensitiveMatch_ThrowsWithSuggestion()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var target = new JsonObject { ["text"] = "hello world" };

        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "replace", target, "X"));
        ex.Message.ShouldContain("Did you mean 'Hello World'");
    }

    [Fact]
    public void Run_TextTarget_MultilineOldText_ReplacesAcrossLines()
    {
        var filePath = CreateTestFile("test.txt", "Line 1\nLine 2\nLine 3\nLine 4");

        var target = new JsonObject { ["text"] = "Line 2\nLine 3" };
        var result = _tool.TestRun(filePath, "replace", target, "Replacement");

        result["status"]!.ToString().ShouldBe("success");
        var content = File.ReadAllText(filePath);
        content.ShouldBe("Line 1\nReplacement\nLine 4");
    }

    [Fact]
    public void Run_TextTarget_ReturnsContextLines()
    {
        var filePath = CreateTestFile("test.txt", "Line 1\nLine 2\nLine 3\nTarget\nLine 5\nLine 6\nLine 7");

        var target = new JsonObject { ["text"] = "Target" };
        var result = _tool.TestRun(filePath, "replace", target, "Replaced");

        result["context"]!.AsArray().Count.ShouldBeGreaterThan(0);
        var contextStr = string.Join("\n", result["context"]!.AsArray().Select(x => x!.ToString()));
        contextStr.ShouldContain("Line 3");
        contextStr.ShouldContain("Line 5");
    }

    [Fact]
    public void Run_TextTarget_ReturnsFileHash()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var target = new JsonObject { ["text"] = "World" };
        var result = _tool.TestRun(filePath, "replace", target, "Universe");

        result["fileHash"]!.ToString().ShouldNotBeNullOrEmpty();
        result["fileHash"]!.ToString().Length.ShouldBe(16);
    }

    [Fact]
    public void Run_TextTarget_ExpectedHashMatches_Succeeds()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");
        var lines = File.ReadAllLines(filePath);
        var hash = ComputeTestHash(lines);

        var target = new JsonObject { ["text"] = "World" };
        var result = _tool.TestRun(filePath, "replace", target, "Universe", expectedHash: hash);

        result["status"]!.ToString().ShouldBe("success");
    }

    [Fact]
    public void Run_TextTarget_ExpectedHashMismatches_Throws()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var target = new JsonObject { ["text"] = "World" };

        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "replace", target, "Universe", expectedHash: "wrong0hash0here"));
        ex.Message.ShouldContain("File hash mismatch");
    }

    [Fact]
    public void Run_TextTarget_InsertOperation_Throws()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var target = new JsonObject { ["text"] = "World" };

        var ex = Should.Throw<ArgumentException>(() =>
            _tool.TestRun(filePath, "insert", target, "New text"));
        ex.Message.ShouldContain("only supports 'replace'");
    }

    [Fact]
    public void Run_TextTarget_DeleteOperation_Throws()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var target = new JsonObject { ["text"] = "World" };

        var ex = Should.Throw<ArgumentException>(() =>
            _tool.TestRun(filePath, "delete", target));
        ex.Message.ShouldContain("only supports 'replace'");
    }

    [Fact]
    public void Run_TextTarget_DisallowedExtension_Throws()
    {
        var filePath = CreateTestFile("test.exe", "content");

        var target = new JsonObject { ["text"] = "old" };

        Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "replace", target, "new"));
    }

    [Fact]
    public void Run_InvalidTarget_ThrowsWithTextInList()
    {
        var filePath = CreateTestFile("test.md", "Some content");

        var target = new JsonObject { ["unknown"] = "value" };

        var ex = Should.Throw<ArgumentException>(() =>
            _tool.TestRun(filePath, "replace", target, "new"));
        ex.Message.ShouldContain("text");
        ex.Message.ShouldContain("lines");
        ex.Message.ShouldContain("heading");
    }

    private string CreateTestFile(string name, string content)
    {
        var path = Path.Combine(_testDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string ComputeTestHash(string[] lines)
    {
        var content = string.Join("\n", lines);
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private class TestableTextEditTool(string vaultPath, string[] allowedExtensions)
        : TextEditTool(vaultPath, allowedExtensions)
    {
        public JsonNode TestRun(string filePath, string operation, JsonObject target, string? content = null,
            string? occurrence = null, bool preserveIndent = true, string? expectedHash = null)
        {
            return Run(filePath, operation, target, content, occurrence, preserveIndent, expectedHash);
        }
    }
}
```

**Dependencies**: `Domain/Tools/Text/TextEditTool.cs` (new)
**Provides**: Comprehensive test coverage for TextEditTool

---

### Tests/Unit/Domain/Text/TextInspectToolTests.cs [edit]

**Purpose**: Update tests to match stripped TextInspectTool (structure-only).

**TOTAL CHANGES**: 2

**Changes**:
1. Remove tests for search and lines modes (lines 73-138)
2. Update TestableTextInspectTool wrapper to match new single-param signature (lines 197-209)

**Reference Implementation**:
```csharp
using System.Text.Json.Nodes;
using Domain.Tools.Text;
using Shouldly;

namespace Tests.Unit.Domain.Text;

public class TextInspectToolTests : IDisposable
{
    private readonly string _testDir;
    private readonly TestableTextInspectTool _tool;

    public TextInspectToolTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"text-inspect-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
        _tool = new TestableTextInspectTool(_testDir, [".md", ".txt", ".json"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    [Fact]
    public void Run_StructureMode_ReturnsMarkdownStructure()
    {
        var content = """
                      ---
                      title: Test
                      ---
                      # Heading 1
                      Some content
                      ## Heading 2
                      ```csharp
                      var x = 1;
                      ```
                      """;
        var filePath = CreateTestFile("test.md", content);

        var result = _tool.TestRun(filePath);

        result["format"]!.ToString().ShouldBe("markdown");
        result["totalLines"]!.GetValue<int>().ShouldBe(9);

        var structure = result["structure"]!;
        structure["frontmatter"]!["keys"]!.AsArray().Count.ShouldBe(1);
        structure["headings"]!.AsArray().Count.ShouldBe(2);
        structure["codeBlocks"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public void Run_StructureMode_PlainText_ReturnsSections()
    {
        var content = """
                      [database]
                      host=localhost

                      [cache]
                      enabled=true
                      """;
        var filePath = CreateTestFile("config.txt", content);

        var result = _tool.TestRun(filePath);

        result["format"]!.ToString().ShouldBe("text");
        var structure = result["structure"]!;
        structure["sections"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public void Run_DisallowedExtension_ThrowsException()
    {
        var filePath = CreateTestFile("script.ps1", "Get-Process");

        Should.Throw<InvalidOperationException>(() => _tool.TestRun(filePath))
            .Message.ShouldContain("not allowed");
    }

    [Fact]
    public void Run_PathOutsideVault_ThrowsException()
    {
        Should.Throw<UnauthorizedAccessException>(() => _tool.TestRun("/etc/passwd"));
    }

    [Fact]
    public void Run_StructureMode_ReturnsFileHash()
    {
        var content = """
                      # Test Document
                      Some content here.
                      """;
        var filePath = CreateTestFile("test.md", content);

        var result = _tool.TestRun(filePath);

        result["fileHash"].ShouldNotBeNull();
        var hash = result["fileHash"]!.ToString();
        hash.Length.ShouldBe(16);
        hash.ShouldMatch("^[a-f0-9]{16}$");
    }

    [Fact]
    public void Run_StructureMode_FileHash_ChangesWhenContentChanges()
    {
        var content1 = "# Original Content";
        var filePath = CreateTestFile("mutable.md", content1);

        var result1 = _tool.TestRun(filePath);
        var hash1 = result1["fileHash"]!.ToString();

        var content2 = "# Modified Content";
        File.WriteAllText(filePath, content2);

        var result2 = _tool.TestRun(filePath);
        var hash2 = result2["fileHash"]!.ToString();

        hash1.ShouldNotBe(hash2);
    }

    private string CreateTestFile(string name, string content)
    {
        var path = Path.Combine(_testDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private class TestableTextInspectTool(string vaultPath, string[] allowedExtensions)
        : TextInspectTool(vaultPath, allowedExtensions)
    {
        public JsonNode TestRun(string filePath)
        {
            return Run(filePath);
        }
    }
}
```

**Dependencies**: `Domain/Tools/Text/TextInspectTool.cs` (edited)
**Provides**: Updated test coverage for structure-only TextInspectTool

---

### Tests/Unit/Domain/Text/TextSearchToolTests.cs [edit]

**Purpose**: Add tests for single-file search via filePath parameter and rename path to directoryPath.

**TOTAL CHANGES**: 2

**Changes**:
1. Update TestableTextSearchTool wrapper to match new Run signature (add filePath, rename path to directoryPath)
2. Add new test methods for single-file search

**Reference Implementation**:
```csharp
using System.Text.Json.Nodes;
using Domain.Tools.Text;
using Shouldly;

namespace Tests.Unit.Domain.Text;

public class TextSearchToolTests : IDisposable
{
    private readonly string _testDir;
    private readonly TestableTextSearchTool _tool;

    public TextSearchToolTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"text-search-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
        _tool = new TestableTextSearchTool(_testDir, [".md", ".txt"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    [Fact]
    public void Run_FindsMatchesAcrossMultipleFiles()
    {
        CreateTestFile("doc1.md", "# About Kubernetes\nKubernetes is great");
        CreateTestFile("doc2.md", "# Setup\nNo match here");
        CreateTestFile("doc3.md", "# Config\nConfigure kubernetes cluster");

        var result = _tool.TestRun("kubernetes");

        result["filesWithMatches"]!.GetValue<int>().ShouldBe(2);
        result["totalMatches"]!.GetValue<int>().ShouldBe(3);
    }

    [Fact]
    public void Run_WithFilePattern_FiltersFiles()
    {
        CreateTestFile("readme.md", "Important info");
        CreateTestFile("notes.txt", "Important notes");

        var result = _tool.TestRun("Important", filePattern: "*.md");

        result["filesWithMatches"]!.GetValue<int>().ShouldBe(1);
        result["results"]!.AsArray()[0]!["file"]!.ToString().ShouldEndWith(".md");
    }

    [Fact]
    public void Run_WithSubdirectory_SearchesRecursively()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, "subdir"));
        CreateTestFile("root.md", "Target word");
        CreateTestFile("subdir/nested.md", "Another target");

        var result = _tool.TestRun("target");

        result["filesWithMatches"]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public void Run_WithRegex_MatchesPattern()
    {
        CreateTestFile("todos.md", "TODO: Fix bug\nFIXME: Later\nTODO: Add test");

        var result = _tool.TestRun("TODO:.*", regex: true);

        result["totalMatches"]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public void Run_IncludesNearestHeading()
    {
        CreateTestFile("doc.md", "# Introduction\nSome text\n## Setup\nFind this target");

        var result = _tool.TestRun("target");

        var match = result["results"]!.AsArray()[0]!["matches"]!.AsArray()[0]!;
        match["section"]!.ToString().ShouldBe("Setup");
    }

    [Fact]
    public void Run_WithContextLines_IncludesContext()
    {
        CreateTestFile("doc.md", "Line 1\nLine 2\nTarget line\nLine 4\nLine 5");

        var result = _tool.TestRun("Target", contextLines: 2);

        var match = result["results"]!.AsArray()[0]!["matches"]!.AsArray()[0]!;
        var context = match["context"]!;
        context["before"]!.AsArray().Count.ShouldBe(2);
        context["after"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public void Run_RespectsMaxResults()
    {
        var content = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"match line {i}"));
        CreateTestFile("many.md", content);

        var result = _tool.TestRun("match", maxResults: 10);

        result["totalMatches"]!.GetValue<int>().ShouldBe(10);
        result["truncated"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void Run_CaseInsensitiveByDefault()
    {
        CreateTestFile("doc.md", "KUBERNETES\nkubernetes\nKubernetes");

        var result = _tool.TestRun("kubernetes");

        result["totalMatches"]!.GetValue<int>().ShouldBe(3);
    }

    [Fact]
    public void Run_NoMatches_ReturnsEmptyResults()
    {
        CreateTestFile("doc.md", "Some content here");

        var result = _tool.TestRun("nonexistent");

        result["filesWithMatches"]!.GetValue<int>().ShouldBe(0);
        result["totalMatches"]!.GetValue<int>().ShouldBe(0);
        result["results"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public void Run_SkipsDisallowedExtensions()
    {
        CreateTestFile("doc.md", "Find this");
        CreateTestFile("script.ps1", "Find this too");

        var result = _tool.TestRun("Find");

        result["filesWithMatches"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public void Run_RelativePathsInResults()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, "docs"));
        CreateTestFile("docs/guide.md", "Target content");

        var result = _tool.TestRun("Target");

        var file = result["results"]!.AsArray()[0]!["file"]!.ToString();
        file.ShouldBe("docs/guide.md");
        file.ShouldNotContain("\\");
    }

    // --- Single-file search tests ---

    [Fact]
    public void Run_WithFilePath_SearchesSingleFile()
    {
        CreateTestFile("target.md", "Find this line\nAnd this one too");
        CreateTestFile("other.md", "Find this also");

        var filePath = Path.Combine(_testDir, "target.md");
        var result = _tool.TestRun("Find", filePath: filePath);

        result["filesSearched"]!.GetValue<int>().ShouldBe(1);
        result["filesWithMatches"]!.GetValue<int>().ShouldBe(1);
        result["totalMatches"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public void Run_WithFilePath_IgnoresDirectoryPathAndFilePattern()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, "subdir"));
        CreateTestFile("subdir/target.md", "Find this line");
        CreateTestFile("other.md", "Find this also");

        var filePath = Path.Combine(_testDir, "subdir", "target.md");
        var result = _tool.TestRun("Find", filePath: filePath, filePattern: "*.txt", directoryPath: "/nonexistent");

        result["filesSearched"]!.GetValue<int>().ShouldBe(1);
        result["totalMatches"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public void Run_WithFilePath_FileNotFound_Throws()
    {
        var filePath = Path.Combine(_testDir, "nonexistent.md");

        Should.Throw<FileNotFoundException>(() =>
            _tool.TestRun("query", filePath: filePath));
    }

    [Fact]
    public void Run_WithFilePath_OutsideVault_Throws()
    {
        Should.Throw<UnauthorizedAccessException>(() =>
            _tool.TestRun("query", filePath: "/etc/passwd"));
    }

    [Fact]
    public void Run_WithFilePath_DisallowedExtension_Throws()
    {
        CreateTestFile("script.ps1", "Find this");

        var filePath = Path.Combine(_testDir, "script.ps1");

        Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun("Find", filePath: filePath));
    }

    [Fact]
    public void Run_WithFilePath_NoMatches_ReturnsEmpty()
    {
        CreateTestFile("target.md", "No matching content here");

        var filePath = Path.Combine(_testDir, "target.md");
        var result = _tool.TestRun("nonexistent", filePath: filePath);

        result["filesWithMatches"]!.GetValue<int>().ShouldBe(0);
        result["totalMatches"]!.GetValue<int>().ShouldBe(0);
    }

    [Fact]
    public void Run_WithFilePath_WithRegex_MatchesPattern()
    {
        CreateTestFile("target.md", "TODO: Fix bug\nFIXME: Later\nTODO: Add test");

        var filePath = Path.Combine(_testDir, "target.md");
        var result = _tool.TestRun("TODO:.*", regex: true, filePath: filePath);

        result["totalMatches"]!.GetValue<int>().ShouldBe(2);
    }

    private void CreateTestFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_testDir, relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(fullPath, content);
    }

    private class TestableTextSearchTool(string vaultPath, string[] allowedExtensions)
        : TextSearchTool(vaultPath, allowedExtensions)
    {
        public JsonNode TestRun(
            string query,
            bool regex = false,
            string? filePath = null,
            string? filePattern = null,
            string directoryPath = "/",
            int maxResults = 50,
            int contextLines = 1)
        {
            return Run(query, regex, filePath, filePattern, directoryPath, maxResults, contextLines);
        }
    }
}
```

**Dependencies**: `Domain/Tools/Text/TextSearchTool.cs` (edited)
**Provides**: Test coverage for single-file search and updated parameters

---

### Tests/Unit/Domain/Text/TextReadToolTests.cs [edit]

**Purpose**: Update test that validates the "search" target is not in the valid target list message -- the message should now include "text" is no longer in it (TextRead never had text target). No actual changes needed since the test at line 106-124 already validates the correct behavior for TextRead.

**TOTAL CHANGES**: 0

This file requires no changes. The existing tests correctly validate TextReadTool behavior. The description change in TextReadTool is a const string that doesn't affect test behavior.

**Dependencies**: `Domain/Tools/Text/TextReadTool.cs` (edited -- description only)
**Provides**: Existing test coverage remains valid

---

### File Deletions

The following files must be deleted after the new TextEditTool and its tests are created:

- `Domain/Tools/Text/TextPatchTool.cs` -- replaced by TextEditTool
- `Domain/Tools/Text/TextReplaceTool.cs` -- replaced by TextEditTool
- `McpServerText/McpTools/McpTextPatchTool.cs` -- replaced by McpTextEditTool
- `McpServerText/McpTools/McpTextReplaceTool.cs` -- replaced by McpTextEditTool
- `Tests/Unit/Domain/Text/TextPatchToolTests.cs` -- replaced by TextEditToolTests
- `Tests/Unit/Domain/Text/TextReplaceToolTests.cs` -- replaced by TextEditToolTests

## Dependency Graph

> Files in the same phase can execute in parallel.

| Phase | File | Action | Depends On |
|-------|------|--------|------------|
| 1 | `Domain/Tools/Text/TextEditTool.cs` | create | -- |
| 1 | `Domain/Tools/Text/TextReadTool.cs` | edit | -- |
| 1 | `Domain/Tools/Text/TextCreateTool.cs` | edit | -- |
| 1 | `Domain/Tools/Files/ListFilesTool.cs` | edit | -- |
| 1 | `Domain/Tools/Files/RemoveFileTool.cs` | edit | -- |
| 2 | `Tests/Unit/Domain/Text/TextEditToolTests.cs` | create | `Domain/Tools/Text/TextEditTool.cs` |
| 2 | `Domain/Tools/Text/TextInspectTool.cs` | edit | -- |
| 2 | `Domain/Tools/Text/TextSearchTool.cs` | edit | -- |
| 3 | `Tests/Unit/Domain/Text/TextInspectToolTests.cs` | edit | `Domain/Tools/Text/TextInspectTool.cs` |
| 3 | `Tests/Unit/Domain/Text/TextSearchToolTests.cs` | edit | `Domain/Tools/Text/TextSearchTool.cs` |
| 3 | `McpServerText/McpTools/McpTextEditTool.cs` | create | `Domain/Tools/Text/TextEditTool.cs` |
| 3 | `McpServerText/McpTools/McpTextInspectTool.cs` | edit | `Domain/Tools/Text/TextInspectTool.cs` |
| 3 | `McpServerText/McpTools/McpTextSearchTool.cs` | edit | `Domain/Tools/Text/TextSearchTool.cs` |
| 3 | `McpServerText/McpTools/McpTextListFilesTool.cs` | edit | `Domain/Tools/Files/ListFilesTool.cs` |
| 3 | `McpServerText/McpTools/McpRemoveFileTool.cs` | edit | `Domain/Tools/Files/RemoveFileTool.cs` |
| 4 | `McpServerText/Modules/ConfigModule.cs` | edit | `McpServerText/McpTools/McpTextEditTool.cs` |
| 5 | Delete `Domain/Tools/Text/TextPatchTool.cs` | delete | `Domain/Tools/Text/TextEditTool.cs`, `Tests/Unit/Domain/Text/TextEditToolTests.cs` |
| 5 | Delete `Domain/Tools/Text/TextReplaceTool.cs` | delete | `Domain/Tools/Text/TextEditTool.cs`, `Tests/Unit/Domain/Text/TextEditToolTests.cs` |
| 5 | Delete `McpServerText/McpTools/McpTextPatchTool.cs` | delete | `McpServerText/McpTools/McpTextEditTool.cs` |
| 5 | Delete `McpServerText/McpTools/McpTextReplaceTool.cs` | delete | `McpServerText/McpTools/McpTextEditTool.cs` |
| 5 | Delete `Tests/Unit/Domain/Text/TextPatchToolTests.cs` | delete | `Tests/Unit/Domain/Text/TextEditToolTests.cs` |
| 5 | Delete `Tests/Unit/Domain/Text/TextReplaceToolTests.cs` | delete | `Tests/Unit/Domain/Text/TextEditToolTests.cs` |

## Exit Criteria

### Test Commands
```bash
dotnet test Tests/ --filter "FullyQualifiedName~TextEditToolTests"
dotnet test Tests/ --filter "FullyQualifiedName~TextInspectToolTests"
dotnet test Tests/ --filter "FullyQualifiedName~TextSearchToolTests"
dotnet test Tests/ --filter "FullyQualifiedName~TextReadToolTests"
dotnet build McpServerText/
```

### Success Conditions
- [ ] All TextEditToolTests pass (28 tests covering positional and text-match targeting)
- [ ] All TextInspectToolTests pass (6 tests for structure-only mode)
- [ ] All TextSearchToolTests pass (19 tests including 7 new single-file search tests)
- [ ] All TextReadToolTests pass (unchanged, 7 tests)
- [ ] McpServerText builds successfully with McpTextEditTool registered
- [ ] TextPatchTool, TextReplaceTool, McpTextPatchTool, McpTextReplaceTool deleted
- [ ] TextPatchToolTests, TextReplaceToolTests deleted
- [ ] No compilation errors across the entire solution
- [ ] All requirements (1-13) satisfied

### Verification Script
```bash
dotnet build && dotnet test Tests/
```
