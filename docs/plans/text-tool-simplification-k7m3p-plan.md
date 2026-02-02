# Plan: Text Tool Simplification

**Design**: `docs/designs/2026-02-02-text-tool-simplification-design.md`

## Summary

Simplify the McpServerText tools to match Claude Code's agentic patterns: flat scalar parameters, whole-file reads, and exact string matching for edits. This reduces 5 tools to 4, eliminates JSON string parameters, removes all positional/structural targeting from TextEdit (keeping only oldString/newString), rewrites TextRead to return whole files with optional pagination, adds an `overwrite` parameter to TextCreate, adds an `outputMode` parameter to TextSearch, and deletes TextInspect along with MarkdownParser.

## Files

> **Note**: This is the canonical file list.

### Files to Delete
- `Domain/Tools/Text/TextInspectTool.cs`
- `Domain/Tools/Text/MarkdownParser.cs`
- `Domain/Tools/Text/MarkdownStructure.cs`
- `McpServerText/McpTools/McpTextInspectTool.cs`
- `Tests/Unit/Domain/Text/TextInspectToolTests.cs`
- `Tests/Unit/Domain/Text/MarkdownParserTests.cs`

### Files to Rewrite
- `Domain/Tools/Text/TextReadTool.cs`
- `Domain/Tools/Text/TextEditTool.cs`
- `McpServerText/McpTools/McpTextReadTool.cs`
- `McpServerText/McpTools/McpTextEditTool.cs`
- `Tests/Unit/Domain/Text/TextReadToolTests.cs`
- `Tests/Unit/Domain/Text/TextEditToolTests.cs`

### Files to Edit
- `Domain/Tools/Text/TextCreateTool.cs`
- `Domain/Tools/Text/TextSearchTool.cs`
- `McpServerText/McpTools/McpTextCreateTool.cs`
- `McpServerText/McpTools/McpTextSearchTool.cs`
- `McpServerText/Modules/ConfigModule.cs`
- `Tests/Unit/Domain/Text/TextCreateToolTests.cs`
- `Tests/Unit/Domain/Text/TextSearchToolTests.cs`

## Code Context

### TextToolBase (`Domain/Tools/Text/TextToolBase.cs`)
- Lines 1-56: Abstract base class with `ValidateAndResolvePath(string filePath)`, `ComputeFileHash(string[] lines)`, `ValidateExpectedHash(string[] lines, string? expectedHash)`
- TextEditTool inherits from this; TextReadTool does NOT (has its own `ValidateAndResolvePath` at lines 198-222)
- TextCreateTool and TextSearchTool have their own path validation methods

### TextEditTool (`Domain/Tools/Text/TextEditTool.cs`)
- Lines 1-562: Contains two code paths: `RunTextReplace` (lines 60-129) for text targets, `RunPositionalEdit` (lines 131-218) for positional targets
- Text replace helpers (lines 478-561): `FindAllOccurrences`, `FindCaseInsensitiveSuggestion`, `ApplyTextReplacement`, `ComputeAffectedLines`, `GetTextReplaceContextLines`
- The new simplified tool keeps ONLY the text replace path, removes positional editing entirely
- `occurrence` parameter currently supports "first", "last", "all", numeric; design replaces with boolean `replaceAll`

### TextReadTool (`Domain/Tools/Text/TextReadTool.cs`)
- Lines 1-223: Takes `JsonObject target` with lines/heading/codeBlock/anchor/section targeting
- Has its own `ValidateAndResolvePath` (lines 198-222) separate from TextToolBase
- MaxReturnLines = 200 (line 32); design changes to 500
- Dependencies on MarkdownParser at lines 123, 149, 163, 179 — all removed in rewrite

### TextCreateTool (`Domain/Tools/Text/TextCreateTool.cs`)
- Lines 25-46: `Run(string filePath, string content, bool createDirectories = true)`
- Line 58-64: `ValidateNotExists` — needs conditional bypass when `overwrite=true`
- Line 36: `File.WriteAllText` — used for both create and overwrite paths

### TextSearchTool (`Domain/Tools/Text/TextSearchTool.cs`)
- Lines 44-76: `Run(...)` method with 7 params
- Lines 246-305: `BuildResultJson` and JSON serializers — need "files_only" mode that skips match details
- Line 33-42: Private records `SearchParams`, `FileMatch`, `MatchResult` — `FileMatch` needs new constructor path for files_only

### ConfigModule (`McpServerText/Modules/ConfigModule.cs`)
- Line 56: `.WithTools<McpTextInspectTool>()` — must be removed

### MCP wrappers
- `McpTextEditTool.cs` lines 17-39: Parses JSON `target` string, calls `Run(filePath, operation, targetObj, ...)` — entire McpRun needs rewrite to flat params
- `McpTextReadTool.cs` lines 17-28: Parses JSON `target` string — entire McpRun needs rewrite to flat params
- `McpTextCreateTool.cs` lines 16-25: Simple pass-through — add `overwrite` param
- `McpTextSearchTool.cs` lines 16-33: Simple pass-through — add `outputMode` param

### Test patterns
- All test classes use `TestableXxxTool` inner class wrapper pattern to expose protected `Run` method
- Use `Shouldly` assertions, `IDisposable` cleanup, temp directories
- Naming: `Method_Scenario_ExpectedResult`

## External Context

N/A — no external libraries or APIs needed. All changes are internal refactoring of existing domain tools.

## Architectural Narrative

### Task

Simplify the McpServerText tool suite from 5 tools to 4 by:
1. Rewriting TextRead to serve whole files with optional offset/limit pagination (no targeting)
2. Rewriting TextEdit to use flat oldString/newString parameters (no positional targeting, no JSON target object)
3. Extending TextCreate with an `overwrite` parameter
4. Extending TextSearch with an `outputMode` parameter ("content" or "files_only")
5. Deleting TextInspect, MarkdownParser, and MarkdownStructure (no longer needed)

### Architecture

The text tools follow a two-layer pattern:
- **Domain layer** (`Domain/Tools/Text/`): Business logic classes with `protected Run(...)` methods
- **MCP layer** (`McpServerText/McpTools/`): Thin wrappers inheriting domain tools, exposing `McpRun(...)` with MCP attributes

TextEditTool and TextInspectTool inherit from `TextToolBase` (path validation, hash computation). TextReadTool, TextCreateTool, and TextSearchTool have their own path validation.

### Selected Context

- `Domain/Tools/Text/TextToolBase.cs`: Base class providing `ValidateAndResolvePath`, `ComputeFileHash`, `ValidateExpectedHash` — TextEditTool will continue to inherit from this
- `Domain/Tools/Text/TextEditTool.cs:60-129`: `RunTextReplace` method — the core logic that survives the rewrite
- `Domain/Tools/Text/TextEditTool.cs:480-561`: Helper methods for text replacement — kept in simplified form
- `Domain/Tools/Text/TextCreateTool.cs:25-46`: Current `Run` method — extended with `overwrite` param
- `Domain/Tools/Text/TextSearchTool.cs:44-76`: Current `Run` method — extended with `outputMode` param
- `Infrastructure/Utils/ToolResponse.cs`: `ToolResponse.Create(JsonNode)` used by all MCP wrappers

### Relationships

```
McpTextEditTool -> TextEditTool -> TextToolBase
McpTextReadTool -> TextReadTool (standalone, own path validation)
McpTextCreateTool -> TextCreateTool (standalone, own path validation)
McpTextSearchTool -> TextSearchTool (standalone, own path validation)
ConfigModule registers all MCP tools
```

After changes, MarkdownParser and TextInspectTool are removed from the dependency graph entirely.

### External Context

No external libraries needed.

### Implementation Notes

1. **TextReadTool rewrite**: Currently inherits nothing and has its own `ValidateAndResolvePath`. The rewrite keeps this pattern (no need to inherit TextToolBase since TextRead does not need hash computation or hash validation).
2. **TextEditTool rewrite**: The `RunTextReplace` logic (lines 60-129) is the foundation. The `occurrence` parameter changes from string ("first"/"last"/"all"/numeric) to a boolean `replaceAll`. When `replaceAll=false`, the old text must appear exactly once or the tool fails with the occurrence count. This is a behavior change from the current "first" default.
3. **Atomic writes**: TextEditTool must keep the temp file + move pattern for atomic writes.
4. **MarkdownParser deletion**: After removing all references from TextEditTool and TextReadTool, MarkdownParser.cs and MarkdownStructure.cs can be deleted. No other files in the codebase import these.
5. **TextReadTool line format**: Returns content with line numbers: `"1: first line\n2: second line\n..."` plus trailing metadata with totalLines and fileHash.

### Ambiguities

- None. The design document is explicit about all parameter signatures and behaviors.

### Requirements

1. TextRead returns whole file content with line numbers, respecting optional offset/limit, with 500-line max truncation
2. TextRead returns totalLines and fileHash in trailing metadata
3. TextEdit accepts flat oldString/newString parameters (no JSON target object)
4. TextEdit with replaceAll=false fails if oldString appears more than once, reporting occurrence count
5. TextEdit with replaceAll=true replaces every occurrence
6. TextEdit preserves case-insensitive suggestion on not-found
7. TextEdit uses atomic write (temp file + move)
8. TextCreate supports overwrite=true to overwrite existing files
9. TextCreate with overwrite=false (default) preserves current behavior (fails if file exists)
10. TextSearch supports outputMode="files_only" returning file paths with match counts
11. TextSearch with outputMode="content" (default) preserves current behavior
12. TextInspect tool is deleted from domain and MCP layers
13. MarkdownParser and MarkdownStructure are deleted
14. McpTextInspectTool registration removed from ConfigModule
15. All MCP wrappers use flat scalar parameters (no JSON string parsing)
16. All existing test coverage for preserved behaviors continues to pass

### Constraints

- Domain layer must not import Infrastructure or Agent namespaces
- MCP tools inherit from domain tools and use `[McpServerToolType]`, `[McpServerTool]`, `[Description]` attributes
- No try/catch in MCP tools — global filter in ConfigModule handles errors
- Use Shouldly for test assertions
- Follow TDD: write tests before implementation

### Selected Approach

**Approach**: Complete rewrite of TextRead and TextEdit, incremental modification of TextCreate and TextSearch
**Description**: TextReadTool and TextEditTool are rewritten from scratch with simplified signatures. TextCreateTool gets a new `overwrite` parameter with minimal changes. TextSearchTool gets a new `outputMode` parameter. TextInspect, MarkdownParser, and MarkdownStructure are deleted after all references are removed.
**Rationale**: The design is explicit — these tools need fundamental interface changes, not incremental refactoring. A clean rewrite of TextRead (~50 lines) and TextEdit (~60 lines) is simpler and less error-prone than trying to surgically remove features from 223-line and 562-line files.
**Trade-offs Accepted**: All existing positional targeting tests must be replaced, not preserved. Users of heading/codeBlock/anchor/section targeting lose that functionality.

## Implementation Plan

### Domain/Tools/Text/TextReadTool.cs [rewrite]

**Purpose**: Read whole text files with optional offset/limit pagination and line-numbered output.

**TOTAL CHANGES**: 1 (complete rewrite)

**Changes**:
1. Replace entire file (lines 1-223) with new implementation (~60 lines)

**Implementation Details**:
- Constructor: `TextReadTool(string vaultPath, string[] allowedExtensions)` — keep standalone (no TextToolBase inheritance)
- `Run(string filePath, int? offset = null, int? limit = null) -> JsonNode`
- Keep own `ValidateAndResolvePath` method (same as current lines 198-222)
- MaxReturnLines = 500
- Output format: `"1: first line\n2: second line\n..."` with actual line numbers (offset-aware)
- Trailing metadata: totalLines, fileHash (SHA256, first 16 hex chars lowercase)

**Reference Implementation**:
```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Domain.Tools.Text;

public class TextReadTool(string vaultPath, string[] allowedExtensions)
{
    protected const string Name = "TextRead";

    protected const string Description = """
                                         Reads a text file and returns its content with line numbers.

                                         Returns content formatted as "1: first line\n2: second line\n..." with trailing metadata.
                                         Large files are truncated at 500 lines — use offset and limit for pagination.

                                         Parameters:
                                         - filePath: Path to file (absolute or relative to vault)
                                         - offset: Start from this line number (1-based, default: 1)
                                         - limit: Max lines to return (default: all remaining lines)
                                         """;

    private const int MaxReturnLines = 500;

    protected JsonNode Run(string filePath, int? offset = null, int? limit = null)
    {
        var fullPath = ValidateAndResolvePath(filePath);
        var allLines = File.ReadAllLines(fullPath);
        var totalLines = allLines.Length;

        var startIndex = (offset ?? 1) - 1;
        if (startIndex < 0) startIndex = 0;
        if (startIndex > allLines.Length) startIndex = allLines.Length;

        var remainingLines = allLines.Skip(startIndex).ToArray();
        var requestedLimit = limit ?? remainingLines.Length;
        var effectiveLimit = Math.Min(requestedLimit, MaxReturnLines);
        var selectedLines = remainingLines.Take(effectiveLimit).ToArray();
        var truncated = remainingLines.Length > effectiveLimit;

        var numberedLines = selectedLines
            .Select((line, i) => $"{startIndex + i + 1}: {line}");
        var content = string.Join("\n", numberedLines);

        var fileHash = ComputeFileHash(allLines);

        var result = new JsonObject
        {
            ["filePath"] = fullPath,
            ["content"] = content,
            ["totalLines"] = totalLines,
            ["fileHash"] = fileHash,
            ["truncated"] = truncated
        };

        if (truncated)
        {
            var nextOffset = startIndex + effectiveLimit + 1;
            result["suggestion"] = $"File has more content. Use offset={nextOffset} to continue reading.";
        }

        return result;
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

    private static string ComputeFileHash(string[] lines)
    {
        var content = string.Join("\n", lines);
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
```

**Test File**: `Tests/Unit/Domain/Text/TextReadToolTests.cs`
- Test: `Run_ReturnsWholeFileWithLineNumbers` — Asserts: content starts with "1: " and contains all lines
- Test: `Run_ReturnsFileHash` — Asserts: fileHash is 16-char hex string
- Test: `Run_ReturnsTotalLines` — Asserts: totalLines matches actual line count
- Test: `Run_WithOffset_StartsFromSpecifiedLine` — Asserts: content starts at offset line number
- Test: `Run_WithLimit_ReturnsLimitedLines` — Asserts: only N lines returned
- Test: `Run_WithOffsetAndLimit_ReturnsPaginatedContent` — Asserts: correct slice of file
- Test: `Run_LargeFile_TruncatesAt500Lines` — Asserts: truncated=true, suggestion present
- Test: `Run_SmallFile_NotTruncated` — Asserts: truncated=false, no suggestion
- Test: `Run_FileNotFound_Throws` — Asserts: FileNotFoundException
- Test: `Run_PathOutsideVault_Throws` — Asserts: UnauthorizedAccessException
- Test: `Run_DisallowedExtension_Throws` — Asserts: InvalidOperationException

**Dependencies**: None (standalone, no dependencies on other plan files)
**Provides**: `TextReadTool.Run(string filePath, int? offset, int? limit) -> JsonNode`

---

### Domain/Tools/Text/TextEditTool.cs [rewrite]

**Purpose**: Edit text files using exact string matching (oldString -> newString replacement).

**TOTAL CHANGES**: 1 (complete rewrite)

**Changes**:
1. Replace entire file (lines 1-562) with new implementation (~100 lines)

**Implementation Details**:
- Inherits `TextToolBase(vaultPath, allowedExtensions)` for `ValidateAndResolvePath`, `ComputeFileHash`
- `Run(string filePath, string oldString, string newString, bool replaceAll = false) -> JsonNode`
- No `ValidateExpectedHash` — removed per design (no `expectedHash` param)
- When replaceAll=false: must find exactly 1 occurrence, fail with count if ambiguous
- When replaceAll=true: replace all occurrences
- Case-insensitive suggestion on not-found (preserved behavior)
- Atomic write via temp file + move
- Returns: status, filePath, occurrencesReplaced, affectedLines {start, end}, fileHash

**Reference Implementation**:
```csharp
using System.Text.Json.Nodes;

namespace Domain.Tools.Text;

public class TextEditTool(string vaultPath, string[] allowedExtensions)
    : TextToolBase(vaultPath, allowedExtensions)
{
    protected const string Name = "TextEdit";

    protected const string Description = """
                                         Edits a text file by replacing exact string matches.

                                         Parameters:
                                         - filePath: Path to file (absolute or relative to vault)
                                         - oldString: Exact text to find (case-sensitive)
                                         - newString: Replacement text
                                         - replaceAll: Replace all occurrences (default: false)

                                         When replaceAll is false, oldString must appear exactly once.
                                         If multiple occurrences are found, the tool fails with the count — provide more surrounding context in oldString to disambiguate.

                                         Insert: include surrounding context in oldString, add new lines in newString.
                                         Delete: include content in oldString, omit it from newString.
                                         """;

    protected JsonNode Run(string filePath, string oldString, string newString, bool replaceAll = false)
    {
        var fullPath = ValidateAndResolvePath(filePath);
        var content = File.ReadAllText(fullPath);

        var positions = FindAllOccurrences(content, oldString);

        if (positions.Count == 0)
        {
            var suggestion = FindCaseInsensitiveSuggestion(content, oldString);
            if (suggestion is not null)
            {
                throw new InvalidOperationException(
                    $"Text '{Truncate(oldString, 100)}' not found (case-sensitive). Did you mean '{Truncate(suggestion, 100)}'?");
            }

            throw new InvalidOperationException($"Text '{Truncate(oldString, 100)}' not found in file.");
        }

        if (!replaceAll && positions.Count > 1)
        {
            throw new InvalidOperationException(
                $"Found {positions.Count} occurrences of the specified text. Provide more surrounding context in oldString to disambiguate, or set replaceAll=true.");
        }

        var replacedContent = replaceAll
            ? content.Replace(oldString, newString, StringComparison.Ordinal)
            : ReplaceFirst(content, oldString, newString, positions[0]);

        var replacedCount = replaceAll ? positions.Count : 1;

        var tempPath = fullPath + ".tmp";
        File.WriteAllText(tempPath, replacedContent);
        File.Move(tempPath, fullPath, overwrite: true);

        var (startLine, endLine) = ComputeAffectedLines(content, positions[0], oldString.Length);
        var updatedLines = File.ReadAllLines(fullPath);
        var fileHash = ComputeFileHash(updatedLines);

        return new JsonObject
        {
            ["status"] = "success",
            ["filePath"] = fullPath,
            ["occurrencesReplaced"] = replacedCount,
            ["affectedLines"] = new JsonObject
            {
                ["start"] = startLine,
                ["end"] = endLine
            },
            ["fileHash"] = fileHash
        };
    }

    private static string ReplaceFirst(string content, string oldString, string newString, int position)
    {
        return content[..position] + newString + content[(position + oldString.Length)..];
    }

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

    private static (int StartLine, int EndLine) ComputeAffectedLines(string content, int position, int oldLength)
    {
        var startLine = content[..position].Count(c => c == '\n') + 1;
        var oldTextContent = content.Substring(position, oldLength);
        var linesInOld = oldTextContent.Count(c => c == '\n');
        return (startLine, startLine + linesInOld);
    }

    private static string Truncate(string text, int maxLength)
    {
        return text.Length > maxLength ? text[..maxLength] + "..." : text;
    }
}
```

**Test File**: `Tests/Unit/Domain/Text/TextEditToolTests.cs`
- Test: `Run_SingleOccurrence_ReplacesText` — Asserts: content replaced, status=success
- Test: `Run_MultipleOccurrences_ReplaceAllFalse_Throws` — Asserts: InvalidOperationException with count
- Test: `Run_MultipleOccurrences_ReplaceAllTrue_ReplacesAll` — Asserts: all occurrences replaced
- Test: `Run_NotFound_Throws` — Asserts: InvalidOperationException with "not found"
- Test: `Run_CaseInsensitiveMatch_ThrowsWithSuggestion` — Asserts: "Did you mean" message
- Test: `Run_MultilineOldString_ReplacesAcrossLines` — Asserts: multiline replacement works
- Test: `Run_ReturnsFileHash` — Asserts: fileHash is 16-char hex string
- Test: `Run_ReturnsAffectedLines` — Asserts: affectedLines.start and .end correct
- Test: `Run_AtomicWrite` — Asserts: no .tmp file remains after success
- Test: `Run_PathOutsideVault_Throws` — Asserts: UnauthorizedAccessException
- Test: `Run_DisallowedExtension_Throws` — Asserts: InvalidOperationException
- Test: `Run_FileNotFound_Throws` — Asserts: FileNotFoundException

**Dependencies**: `Domain/Tools/Text/TextToolBase.cs` (existing, not modified in this plan)
**Provides**: `TextEditTool.Run(string filePath, string oldString, string newString, bool replaceAll) -> JsonNode`

---

### Domain/Tools/Text/TextCreateTool.cs [edit]

**Purpose**: Add `overwrite` parameter to allow overwriting existing files.

**TOTAL CHANGES**: 3

**Changes**:
1. Line 25: Add `bool overwrite = false` parameter to `Run` method signature
2. Lines 29: Make `ValidateNotExists` conditional on `overwrite` being false
3. Lines 58-64: No change to `ValidateNotExists` itself — it is simply skipped when overwrite=true

**Implementation Details**:
- New signature: `Run(string filePath, string content, bool overwrite = false, bool createDirectories = true)`
- When `overwrite=true`, skip the `ValidateNotExists` call
- When `overwrite=false` (default), behavior is identical to current

**Migration Pattern**:
```csharp
// BEFORE (line 25):
protected JsonNode Run(string filePath, string content, bool createDirectories = true)

// AFTER:
protected JsonNode Run(string filePath, string content, bool overwrite = false, bool createDirectories = true)
```

```csharp
// BEFORE (lines 28-29):
ValidateExtension(fullPath);
ValidateNotExists(fullPath, filePath);

// AFTER:
ValidateExtension(fullPath);
if (!overwrite)
{
    ValidateNotExists(fullPath, filePath);
}
```

**Test File**: `Tests/Unit/Domain/Text/TextCreateToolTests.cs`
- Test: `Run_WithOverwriteTrue_OverwritesExistingFile` — Asserts: file content is replaced, status=created
- Test: `Run_WithOverwriteFalse_FileExists_Throws` — Asserts: same as existing `Run_FileAlreadyExists_ThrowsException`

**Dependencies**: None
**Provides**: `TextCreateTool.Run(string filePath, string content, bool overwrite, bool createDirectories) -> JsonNode`

---

### Domain/Tools/Text/TextSearchTool.cs [edit]

**Purpose**: Add `outputMode` parameter supporting "content" (default) and "files_only" modes.

**TOTAL CHANGES**: 4

**Changes**:
1. Line 44: Add `string outputMode = "content"` parameter to `Run` method
2. Lines 72-75: Pass `outputMode` through to `BuildResultJson`
3. Lines 246-265: Modify `BuildResultJson` to accept `outputMode` and build results differently for "files_only"
4. Lines 268-305: Add `ToFileMatchSummaryJson` method for files_only mode

**Implementation Details**:
- New signature: `Run(string query, bool regex, string? filePath, string? filePattern, string directoryPath, int maxResults, int contextLines, string outputMode = "content")`
- When outputMode="files_only": results array contains `{file, matchCount}` per file (no match text, no context)
- When outputMode="content": behavior is identical to current
- Validate outputMode is one of "content" or "files_only"

**Migration Pattern**:
```csharp
// BEFORE (line 44):
protected JsonNode Run(
    string query,
    bool regex = false,
    string? filePath = null,
    string? filePattern = null,
    string directoryPath = "/",
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
    int contextLines = 1,
    string outputMode = "content")
```

```csharp
// BEFORE (line 75):
return BuildResultJson(query, regex, directoryPath, filesSearched, results, totalMatches, maxResults);

// AFTER:
return BuildResultJson(query, regex, directoryPath, filesSearched, results, totalMatches, maxResults, outputMode);
```

Add after `ToMatchResultJson` (after line 300):

```csharp
private static JsonNode ToFileMatchSummaryJson(FileMatch fileMatch)
{
    return new JsonObject
    {
        ["file"] = fileMatch.File,
        ["matchCount"] = fileMatch.Matches.Count
    };
}
```

Modify `BuildResultJson` (lines 246-265):
```csharp
// BEFORE:
private JsonNode BuildResultJson(
    string query, bool regex, string path, int filesSearched,
    List<FileMatch> results, int totalMatches, int maxResults)
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

// AFTER:
private JsonNode BuildResultJson(
    string query, bool regex, string path, int filesSearched,
    List<FileMatch> results, int totalMatches, int maxResults, string outputMode)
{
    if (outputMode is not "content" and not "files_only")
    {
        throw new ArgumentException($"Invalid outputMode '{outputMode}'. Must be 'content' or 'files_only'.");
    }

    var resultMapper = outputMode == "files_only"
        ? (Func<FileMatch, JsonNode>)ToFileMatchSummaryJson
        : ToFileMatchJson;

    return new JsonObject
    {
        ["query"] = query,
        ["regex"] = regex,
        ["path"] = path,
        ["filesSearched"] = filesSearched,
        ["filesWithMatches"] = results.Count,
        ["totalMatches"] = totalMatches,
        ["truncated"] = totalMatches >= maxResults,
        ["results"] = new JsonArray(results.Select(resultMapper).ToArray())
    };
}
```

Also update `RunSingleFileSearch` (line 87) to pass outputMode:
```csharp
// BEFORE:
return BuildResultJson(query, regex, filePath, 1, results, matches.Count, searchParams.MaxResults);

// AFTER:
return BuildResultJson(query, regex, filePath, 1, results, matches.Count, searchParams.MaxResults, outputMode);
```

**Test File**: `Tests/Unit/Domain/Text/TextSearchToolTests.cs`
- Test: `Run_WithOutputModeFilesOnly_ReturnsFilePathsAndCounts` — Asserts: results contain file and matchCount, no matches array
- Test: `Run_WithOutputModeContent_ReturnsFullMatches` — Asserts: same as current default behavior
- Test: `Run_WithInvalidOutputMode_Throws` — Asserts: ArgumentException

**Dependencies**: None
**Provides**: `TextSearchTool.Run(..., string outputMode) -> JsonNode`

---

### McpServerText/McpTools/McpTextReadTool.cs [rewrite]

**Purpose**: Thin MCP wrapper for simplified TextReadTool with flat scalar parameters.

**TOTAL CHANGES**: 1 (complete rewrite)

**Changes**:
1. Replace entire file (lines 1-29) with new implementation using flat params

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
public class McpTextReadTool(McpSettings settings)
    : TextReadTool(settings.VaultPath, settings.AllowedExtensions)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public CallToolResult McpRun(
        [Description("Path to the text file (absolute or relative to vault)")]
        string filePath,
        [Description("Start from this line number (1-based)")]
        int? offset = null,
        [Description("Max lines to return")]
        int? limit = null)
    {
        return ToolResponse.Create(Run(filePath, offset, limit));
    }
}
```

**Dependencies**: `Domain/Tools/Text/TextReadTool.cs` (rewritten in this plan)
**Provides**: `McpTextReadTool.McpRun(string filePath, int? offset, int? limit) -> CallToolResult`

---

### McpServerText/McpTools/McpTextEditTool.cs [rewrite]

**Purpose**: Thin MCP wrapper for simplified TextEditTool with flat scalar parameters.

**TOTAL CHANGES**: 1 (complete rewrite)

**Changes**:
1. Replace entire file (lines 1-41) with new implementation using flat params

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
public class McpTextEditTool(McpSettings settings)
    : TextEditTool(settings.VaultPath, settings.AllowedExtensions)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public CallToolResult McpRun(
        [Description("Path to the text file (absolute or relative to vault)")]
        string filePath,
        [Description("Exact text to find (case-sensitive)")]
        string oldString,
        [Description("Replacement text")]
        string newString,
        [Description("Replace all occurrences (default: false)")]
        bool replaceAll = false)
    {
        return ToolResponse.Create(Run(filePath, oldString, newString, replaceAll));
    }
}
```

**Dependencies**: `Domain/Tools/Text/TextEditTool.cs` (rewritten in this plan)
**Provides**: `McpTextEditTool.McpRun(string filePath, string oldString, string newString, bool replaceAll) -> CallToolResult`

---

### McpServerText/McpTools/McpTextCreateTool.cs [edit]

**Purpose**: Expose new `overwrite` parameter in MCP wrapper.

**TOTAL CHANGES**: 1

**Changes**:
1. Lines 16-25: Add `overwrite` parameter to `McpRun` and pass it to `Run`

**Migration Pattern**:
```csharp
// BEFORE (lines 16-25):
public CallToolResult McpRun(
    [Description("Path for the new file (relative to vault or absolute)")]
    string filePath,
    [Description("Initial content for the file")]
    string content,
    [Description("Create parent directories if they don't exist")]
    bool createDirectories = true)
{
    return ToolResponse.Create(Run(filePath, content, createDirectories));
}

// AFTER:
public CallToolResult McpRun(
    [Description("Path for the new file (relative to vault or absolute)")]
    string filePath,
    [Description("Initial content for the file")]
    string content,
    [Description("Overwrite existing file (default: false)")]
    bool overwrite = false,
    [Description("Create parent directories if they don't exist")]
    bool createDirectories = true)
{
    return ToolResponse.Create(Run(filePath, content, overwrite, createDirectories));
}
```

**Dependencies**: `Domain/Tools/Text/TextCreateTool.cs` (modified in this plan)
**Provides**: `McpTextCreateTool.McpRun(string filePath, string content, bool overwrite, bool createDirectories) -> CallToolResult`

---

### McpServerText/McpTools/McpTextSearchTool.cs [edit]

**Purpose**: Expose new `outputMode` parameter in MCP wrapper.

**TOTAL CHANGES**: 1

**Changes**:
1. Lines 16-33: Add `outputMode` parameter to `McpRun` and pass it to `Run`

**Migration Pattern**:
```csharp
// BEFORE (lines 16-33):
public CallToolResult McpRun(
    [Description("Text or regex pattern to search for")]
    string query,
    [Description("Treat query as regex pattern")]
    bool regex = false,
    [Description("Search within this single file only (ignores directoryPath and filePattern)")]
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

// AFTER:
public CallToolResult McpRun(
    [Description("Text or regex pattern to search for")]
    string query,
    [Description("Treat query as regex pattern")]
    bool regex = false,
    [Description("Search within this single file only (ignores directoryPath and filePattern)")]
    string? filePath = null,
    [Description("Glob pattern to filter files (e.g., '*.md')")]
    string? filePattern = null,
    [Description("Directory to search in (default: entire vault)")]
    string directoryPath = "/",
    [Description("Maximum number of matches to return")]
    int maxResults = 50,
    [Description("Lines of context around each match")]
    int contextLines = 1,
    [Description("Output mode: 'content' for matching lines with context, 'files_only' for file paths with match counts")]
    string outputMode = "content")
{
    return ToolResponse.Create(Run(query, regex, filePath, filePattern, directoryPath, maxResults, contextLines, outputMode));
}
```

**Dependencies**: `Domain/Tools/Text/TextSearchTool.cs` (modified in this plan)
**Provides**: `McpTextSearchTool.McpRun(..., string outputMode) -> CallToolResult`

---

### McpServerText/Modules/ConfigModule.cs [edit]

**Purpose**: Remove McpTextInspectTool registration.

**TOTAL CHANGES**: 2

**Changes**:
1. Line 6: Remove `using McpServerText.McpTools;` — not needed since other usages are qualified (actually this using is shared, keep it)
2. Line 56: Remove `.WithTools<McpTextInspectTool>()`

**Migration Pattern**:
```csharp
// BEFORE (lines 54-59):
// Text tools
.WithTools<McpTextSearchTool>()
.WithTools<McpTextInspectTool>()
.WithTools<McpTextReadTool>()
.WithTools<McpTextEditTool>()
.WithTools<McpTextCreateTool>()

// AFTER:
// Text tools
.WithTools<McpTextSearchTool>()
.WithTools<McpTextReadTool>()
.WithTools<McpTextEditTool>()
.WithTools<McpTextCreateTool>()
```

**Dependencies**: None (TextInspect deletion happens separately)
**Provides**: Updated ConfigModule without TextInspect registration

---

### Tests/Unit/Domain/Text/TextReadToolTests.cs [rewrite]

**Purpose**: Test the simplified TextReadTool with whole-file reading and pagination.

**TOTAL CHANGES**: 1 (complete rewrite)

**Reference Implementation**:
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
    public void Run_ReturnsWholeFileWithLineNumbers()
    {
        var filePath = CreateTestFile("test.txt", "Line A\nLine B\nLine C");

        var result = _tool.TestRun(filePath);

        result["content"]!.ToString().ShouldBe("1: Line A\n2: Line B\n3: Line C");
    }

    [Fact]
    public void Run_ReturnsFileHash()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var result = _tool.TestRun(filePath);

        result["fileHash"]!.ToString().ShouldNotBeNullOrEmpty();
        result["fileHash"]!.ToString().Length.ShouldBe(16);
    }

    [Fact]
    public void Run_ReturnsTotalLines()
    {
        var filePath = CreateTestFile("test.txt", "Line 1\nLine 2\nLine 3\nLine 4\nLine 5");

        var result = _tool.TestRun(filePath);

        result["totalLines"]!.GetValue<int>().ShouldBe(5);
    }

    [Fact]
    public void Run_WithOffset_StartsFromSpecifiedLine()
    {
        var filePath = CreateTestFile("test.txt", "Line 1\nLine 2\nLine 3\nLine 4\nLine 5");

        var result = _tool.TestRun(filePath, offset: 3);

        result["content"]!.ToString().ShouldBe("3: Line 3\n4: Line 4\n5: Line 5");
    }

    [Fact]
    public void Run_WithLimit_ReturnsLimitedLines()
    {
        var filePath = CreateTestFile("test.txt", "Line 1\nLine 2\nLine 3\nLine 4\nLine 5");

        var result = _tool.TestRun(filePath, limit: 2);

        result["content"]!.ToString().ShouldBe("1: Line 1\n2: Line 2");
    }

    [Fact]
    public void Run_WithOffsetAndLimit_ReturnsPaginatedContent()
    {
        var filePath = CreateTestFile("test.txt", "Line 1\nLine 2\nLine 3\nLine 4\nLine 5");

        var result = _tool.TestRun(filePath, offset: 2, limit: 3);

        result["content"]!.ToString().ShouldBe("2: Line 2\n3: Line 3\n4: Line 4");
    }

    [Fact]
    public void Run_LargeFile_TruncatesAt500Lines()
    {
        var content = string.Join("\n", Enumerable.Range(1, 600).Select(i => $"Line {i}"));
        var filePath = CreateTestFile("large.txt", content);

        var result = _tool.TestRun(filePath);

        result["truncated"]!.GetValue<bool>().ShouldBeTrue();
        result["suggestion"]!.ToString().ShouldContain("offset=501");
        result["totalLines"]!.GetValue<int>().ShouldBe(600);
        // Verify only 500 lines in content
        result["content"]!.ToString().Split('\n').Length.ShouldBe(500);
    }

    [Fact]
    public void Run_SmallFile_NotTruncated()
    {
        var filePath = CreateTestFile("test.txt", "Line 1\nLine 2");

        var result = _tool.TestRun(filePath);

        result["truncated"]!.GetValue<bool>().ShouldBeFalse();
        result.AsObject().ContainsKey("suggestion").ShouldBeFalse();
    }

    [Fact]
    public void Run_FileNotFound_Throws()
    {
        Should.Throw<FileNotFoundException>(() =>
            _tool.TestRun(Path.Combine(_testDir, "nonexistent.txt")));
    }

    [Fact]
    public void Run_PathOutsideVault_Throws()
    {
        Should.Throw<UnauthorizedAccessException>(() =>
            _tool.TestRun("/etc/passwd"));
    }

    [Fact]
    public void Run_DisallowedExtension_Throws()
    {
        var filePath = CreateTestFile("test.exe", "content");

        Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath));
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
        public JsonNode TestRun(string filePath, int? offset = null, int? limit = null)
        {
            return Run(filePath, offset, limit);
        }
    }
}
```

**Dependencies**: `Domain/Tools/Text/TextReadTool.cs` (rewritten in this plan)
**Provides**: Test coverage for TextReadTool

---

### Tests/Unit/Domain/Text/TextEditToolTests.cs [rewrite]

**Purpose**: Test the simplified TextEditTool with oldString/newString interface.

**TOTAL CHANGES**: 1 (complete rewrite)

**Reference Implementation**:
```csharp
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

    [Fact]
    public void Run_SingleOccurrence_ReplacesText()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var result = _tool.TestRun(filePath, "World", "Universe");

        result["status"]!.ToString().ShouldBe("success");
        result["occurrencesReplaced"]!.GetValue<int>().ShouldBe(1);
        File.ReadAllText(filePath).ShouldBe("Hello Universe");
    }

    [Fact]
    public void Run_MultipleOccurrences_ReplaceAllFalse_Throws()
    {
        var filePath = CreateTestFile("test.txt", "foo bar foo baz foo");

        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "foo", "FOO"));
        ex.Message.ShouldContain("3 occurrences");
        ex.Message.ShouldContain("disambiguate");
        // File should be unchanged
        File.ReadAllText(filePath).ShouldBe("foo bar foo baz foo");
    }

    [Fact]
    public void Run_MultipleOccurrences_ReplaceAllTrue_ReplacesAll()
    {
        var filePath = CreateTestFile("test.txt", "foo bar foo baz foo");

        var result = _tool.TestRun(filePath, "foo", "FOO", replaceAll: true);

        result["status"]!.ToString().ShouldBe("success");
        result["occurrencesReplaced"]!.GetValue<int>().ShouldBe(3);
        File.ReadAllText(filePath).ShouldBe("FOO bar FOO baz FOO");
    }

    [Fact]
    public void Run_NotFound_Throws()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "Missing", "X"));
        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public void Run_CaseInsensitiveMatch_ThrowsWithSuggestion()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "hello world", "X"));
        ex.Message.ShouldContain("Did you mean");
    }

    [Fact]
    public void Run_MultilineOldString_ReplacesAcrossLines()
    {
        var filePath = CreateTestFile("test.txt", "Line 1\nLine 2\nLine 3\nLine 4");

        var result = _tool.TestRun(filePath, "Line 2\nLine 3", "Replacement");

        result["status"]!.ToString().ShouldBe("success");
        File.ReadAllText(filePath).ShouldBe("Line 1\nReplacement\nLine 4");
    }

    [Fact]
    public void Run_ReturnsFileHash()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var result = _tool.TestRun(filePath, "World", "Universe");

        result["fileHash"]!.ToString().ShouldNotBeNullOrEmpty();
        result["fileHash"]!.ToString().Length.ShouldBe(16);
    }

    [Fact]
    public void Run_ReturnsAffectedLines()
    {
        var filePath = CreateTestFile("test.txt", "Line 1\nLine 2\nTarget\nLine 4");

        var result = _tool.TestRun(filePath, "Target", "Replaced");

        result["affectedLines"]!["start"]!.GetValue<int>().ShouldBe(3);
        result["affectedLines"]!["end"]!.GetValue<int>().ShouldBe(3);
    }

    [Fact]
    public void Run_AtomicWrite_NoTmpFileRemains()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        _tool.TestRun(filePath, "World", "Universe");

        File.Exists(filePath + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public void Run_PathOutsideVault_Throws()
    {
        Should.Throw<UnauthorizedAccessException>(() =>
            _tool.TestRun("/etc/passwd", "old", "new"));
    }

    [Fact]
    public void Run_DisallowedExtension_Throws()
    {
        var filePath = CreateTestFile("test.exe", "content");

        Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "old", "new"));
    }

    [Fact]
    public void Run_FileNotFound_Throws()
    {
        Should.Throw<FileNotFoundException>(() =>
            _tool.TestRun(Path.Combine(_testDir, "nonexistent.txt"), "old", "new"));
    }

    private string CreateTestFile(string name, string content)
    {
        var path = Path.Combine(_testDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private class TestableTextEditTool(string vaultPath, string[] allowedExtensions)
        : TextEditTool(vaultPath, allowedExtensions)
    {
        public JsonNode TestRun(string filePath, string oldString, string newString, bool replaceAll = false)
        {
            return Run(filePath, oldString, newString, replaceAll);
        }
    }
}
```

**Dependencies**: `Domain/Tools/Text/TextEditTool.cs` (rewritten in this plan)
**Provides**: Test coverage for TextEditTool

---

### Tests/Unit/Domain/Text/TextCreateToolTests.cs [edit]

**Purpose**: Add tests for the new `overwrite` parameter.

**TOTAL CHANGES**: 2

**Changes**:
1. Lines 109-118: Update `TestableTextCreateTool.TestRun` signature to include `overwrite` parameter
2. After line 107: Add two new test methods

**Migration Pattern**:
```csharp
// BEFORE (lines 109-118):
private class TestableTextCreateTool(string vaultPath, string[] allowedExtensions)
    : TextCreateTool(vaultPath, allowedExtensions)
{
    public JsonNode TestRun(
        string filePath,
        string content,
        bool createDirectories = true)
    {
        return Run(filePath, content, createDirectories);
    }
}

// AFTER:
private class TestableTextCreateTool(string vaultPath, string[] allowedExtensions)
    : TextCreateTool(vaultPath, allowedExtensions)
{
    public JsonNode TestRun(
        string filePath,
        string content,
        bool overwrite = false,
        bool createDirectories = true)
    {
        return Run(filePath, content, overwrite, createDirectories);
    }
}
```

New tests to add after the existing `Run_LeadingSlash_ResolvesCorrectly` test (after line 107):

```csharp
[Fact]
public void Run_WithOverwriteTrue_OverwritesExistingFile()
{
    File.WriteAllText(Path.Combine(_testDir, "existing.md"), "Old content");

    var result = _tool.TestRun("existing.md", "New content", overwrite: true);

    result["status"]!.ToString().ShouldBe("created");
    File.ReadAllText(Path.Combine(_testDir, "existing.md")).ShouldBe("New content");
}

[Fact]
public void Run_WithOverwriteFalse_FileExists_ThrowsException()
{
    File.WriteAllText(Path.Combine(_testDir, "existing.md"), "Old content");

    var ex = Should.Throw<InvalidOperationException>(() =>
        _tool.TestRun("existing.md", "New content", overwrite: false));
    ex.Message.ShouldContain("already exists");
}
```

**Dependencies**: `Domain/Tools/Text/TextCreateTool.cs` (modified in this plan)
**Provides**: Test coverage for TextCreateTool overwrite feature

---

### Tests/Unit/Domain/Text/TextSearchToolTests.cs [edit]

**Purpose**: Add tests for the new `outputMode` parameter.

**TOTAL CHANGES**: 2

**Changes**:
1. Lines 248-262: Update `TestableTextSearchTool.TestRun` signature to include `outputMode` parameter
2. After line 234: Add three new test methods

**Migration Pattern**:
```csharp
// BEFORE (lines 248-262):
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

// AFTER:
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
        int contextLines = 1,
        string outputMode = "content")
    {
        return Run(query, regex, filePath, filePattern, directoryPath, maxResults, contextLines, outputMode);
    }
}
```

New tests to add after `Run_WithFilePath_WithRegex_MatchesPattern` (after line 234):

```csharp
[Fact]
public void Run_WithOutputModeFilesOnly_ReturnsFilePathsAndCounts()
{
    CreateTestFile("doc1.md", "Hello World\nHello again");
    CreateTestFile("doc2.md", "Hello there");

    var result = _tool.TestRun("Hello", outputMode: "files_only");

    result["filesWithMatches"]!.GetValue<int>().ShouldBe(2);
    var firstResult = result["results"]!.AsArray()[0]!;
    firstResult["matchCount"]!.GetValue<int>().ShouldBeGreaterThan(0);
    firstResult.AsObject().ContainsKey("matches").ShouldBeFalse();
}

[Fact]
public void Run_WithOutputModeContent_ReturnsFullMatches()
{
    CreateTestFile("doc.md", "Hello World");

    var result = _tool.TestRun("Hello", outputMode: "content");

    var firstResult = result["results"]!.AsArray()[0]!;
    firstResult["matches"]!.AsArray().Count.ShouldBeGreaterThan(0);
}

[Fact]
public void Run_WithInvalidOutputMode_Throws()
{
    CreateTestFile("doc.md", "Hello World");

    Should.Throw<ArgumentException>(() =>
        _tool.TestRun("Hello", outputMode: "invalid"));
}
```

**Dependencies**: `Domain/Tools/Text/TextSearchTool.cs` (modified in this plan)
**Provides**: Test coverage for TextSearchTool outputMode feature

---

### File Deletions

The following files are deleted (no code changes, just `git rm`):

- `Domain/Tools/Text/TextInspectTool.cs` — Tool removed from the suite
- `Domain/Tools/Text/MarkdownParser.cs` — No longer referenced after TextRead/TextEdit rewrites
- `Domain/Tools/Text/MarkdownStructure.cs` — Types used only by MarkdownParser
- `McpServerText/McpTools/McpTextInspectTool.cs` — MCP wrapper for deleted tool
- `Tests/Unit/Domain/Text/TextInspectToolTests.cs` — Tests for deleted tool
- `Tests/Unit/Domain/Text/MarkdownParserTests.cs` — Tests for deleted parser

## Dependency Graph

> Files in the same phase can execute in parallel.

| Phase | File | Action | Depends On |
|-------|------|--------|------------|
| 1 | `Tests/Unit/Domain/Text/TextReadToolTests.cs` | rewrite | — |
| 1 | `Tests/Unit/Domain/Text/TextEditToolTests.cs` | rewrite | — |
| 1 | `Tests/Unit/Domain/Text/TextCreateToolTests.cs` | edit | — |
| 1 | `Tests/Unit/Domain/Text/TextSearchToolTests.cs` | edit | — |
| 2 | `Domain/Tools/Text/TextReadTool.cs` | rewrite | `Tests/Unit/Domain/Text/TextReadToolTests.cs` |
| 2 | `Domain/Tools/Text/TextEditTool.cs` | rewrite | `Tests/Unit/Domain/Text/TextEditToolTests.cs` |
| 2 | `Domain/Tools/Text/TextCreateTool.cs` | edit | `Tests/Unit/Domain/Text/TextCreateToolTests.cs` |
| 2 | `Domain/Tools/Text/TextSearchTool.cs` | edit | `Tests/Unit/Domain/Text/TextSearchToolTests.cs` |
| 3 | `McpServerText/McpTools/McpTextReadTool.cs` | rewrite | `Domain/Tools/Text/TextReadTool.cs` |
| 3 | `McpServerText/McpTools/McpTextEditTool.cs` | rewrite | `Domain/Tools/Text/TextEditTool.cs` |
| 3 | `McpServerText/McpTools/McpTextCreateTool.cs` | edit | `Domain/Tools/Text/TextCreateTool.cs` |
| 3 | `McpServerText/McpTools/McpTextSearchTool.cs` | edit | `Domain/Tools/Text/TextSearchTool.cs` |
| 4 | `McpServerText/Modules/ConfigModule.cs` | edit | — |
| 4 | `Domain/Tools/Text/TextInspectTool.cs` | delete | — |
| 4 | `Domain/Tools/Text/MarkdownParser.cs` | delete | — |
| 4 | `Domain/Tools/Text/MarkdownStructure.cs` | delete | — |
| 4 | `McpServerText/McpTools/McpTextInspectTool.cs` | delete | — |
| 4 | `Tests/Unit/Domain/Text/TextInspectToolTests.cs` | delete | — |
| 4 | `Tests/Unit/Domain/Text/MarkdownParserTests.cs` | delete | — |

## Exit Criteria

### Test Commands
```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TextReadToolTests|FullyQualifiedName~TextEditToolTests|FullyQualifiedName~TextCreateToolTests|FullyQualifiedName~TextSearchToolTests"
dotnet build McpServerText/McpServerText.csproj
```

### Success Conditions
- [ ] All TextReadToolTests pass (11 tests)
- [ ] All TextEditToolTests pass (12 tests)
- [ ] All TextCreateToolTests pass (10 tests including 2 new)
- [ ] All TextSearchToolTests pass (17 tests including 3 new)
- [ ] McpServerText project builds without errors
- [ ] No references to MarkdownParser, TextInspect, or positional targeting remain in codebase
- [ ] All 6 deleted files are removed from disk
- [ ] All requirements (1-16) are satisfied

### Verification Script
```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TextReadToolTests|FullyQualifiedName~TextEditToolTests|FullyQualifiedName~TextCreateToolTests|FullyQualifiedName~TextSearchToolTests" && dotnet build McpServerText/McpServerText.csproj
```
