# Plan: Text File Edit Improvements

**Design**: `docs/designs/2026-02-02-text-file-edit-improvements-design.md`
**Branch**: `text-file-edit-improvements`

## Overview

Improve McpServerText editing accuracy by adding a TextReplace tool, appendToSection target, context windows in responses, file hash staleness detection, and removing confusing/redundant TextPatch targets and operations.

## Steps

### Step 1: Extract shared ComputeFileHash and ValidateAndResolvePath utilities

**Files:**
- Modify `Domain/Tools/Text/TextPatchTool.cs`
- Modify `Domain/Tools/Text/TextInspectTool.cs`

**Why:** Both TextPatchTool and TextInspectTool have identical `ValidateAndResolvePath` methods. TextReplaceTool will also need it. Extract to a shared base class or static helper to avoid triple duplication. Also add `ComputeFileHash` as a shared utility since it will be used by all three tools.

**What:**
1. Create a base class `TextToolBase(string vaultPath, string[] allowedExtensions)` in `Domain/Tools/Text/TextToolBase.cs` containing:
   - `ValidateAndResolvePath(string filePath)` (move from TextPatchTool)
   - `static string ComputeFileHash(string[] lines)` using SHA256, returning first 16 hex chars
   - `static void ValidateExpectedHash(string[] lines, string? expectedHash)` that throws if hash mismatches
2. Update `TextPatchTool` to inherit from `TextToolBase` instead of having its own path validation
3. Update `TextInspectTool` to inherit from `TextToolBase` instead of having its own path validation

**Tests:**
- Existing tests in `TextPatchToolTests` and `TextInspectToolTests` must continue to pass (path validation, unauthorized access, disallowed extensions)
- Add test: `ComputeFileHash_SameContent_ReturnsSameHash`
- Add test: `ComputeFileHash_DifferentContent_ReturnsDifferentHash`
- Add test: `ValidateExpectedHash_Matching_DoesNotThrow`
- Add test: `ValidateExpectedHash_Mismatching_ThrowsWithCurrentHash`

**Test file:** `Tests/Unit/Domain/Text/TextToolBaseTests.cs`

---

### Step 2: Create TextReplaceTool domain tool

**Files:**
- Create `Domain/Tools/Text/TextReplaceTool.cs`

**Why:** The core search-and-replace logic, independent of MCP concerns.

**What:**
1. Create `TextReplaceTool(string vaultPath, string[] allowedExtensions) : TextToolBase(vaultPath, allowedExtensions)` with:
   - `protected const string Name = "TextReplace"`
   - `protected const string Description` (from design doc)
   - `protected JsonNode Run(string filePath, string oldText, string newText, string occurrence = "first", string? expectedHash = null)`
2. Implementation:
   - Read file as single string with `File.ReadAllText`
   - Call `ValidateExpectedHash` if expectedHash provided
   - Find all occurrences of `oldText` using `IndexOf` in a loop on the full content string
   - If no matches: try case-insensitive search and throw with suggestion
   - Based on `occurrence` parameter:
     - `"first"`: replace first occurrence
     - `"last"`: replace last occurrence
     - `"all"`: replace all occurrences
     - Numeric string: parse as int, replace Nth (1-based), error if N > total
   - Write atomically (temp file + Move pattern)
   - Compute affected line range from the replacement position in the content
   - Return JSON response with: status, filePath, occurrencesFound, occurrencesReplaced, affectedLines, preview (before/after, truncated at 200 chars), context (3 lines before/after), fileHash, note (if other occurrences exist)

**Tests (write FIRST per TDD):**
- `Run_SingleOccurrence_ReplacesText`
- `Run_MultipleOccurrences_ReplacesFirst_ByDefault`
- `Run_MultipleOccurrences_ReplacesLast`
- `Run_MultipleOccurrences_ReplacesAll`
- `Run_MultipleOccurrences_ReplacesNth`
- `Run_NthOccurrence_ExceedsTotal_Throws`
- `Run_TextNotFound_ThrowsWithMessage`
- `Run_TextNotFound_CaseInsensitiveMatch_ThrowsWithSuggestion`
- `Run_MultilineOldText_ReplacesAcrossLines`
- `Run_ReturnsContextLines`
- `Run_ReturnsFileHash`
- `Run_ExpectedHashMatches_Succeeds`
- `Run_ExpectedHashMismatches_Throws`
- `Run_FirstOccurrence_NoteIncludesOtherLocations`
- `Run_PathOutsideVault_Throws`
- `Run_DisallowedExtension_Throws`

**Test file:** `Tests/Unit/Domain/Text/TextReplaceToolTests.cs`

---

### Step 3: Create McpTextReplaceTool MCP wrapper

**Files:**
- Create `McpServerText/McpTools/McpTextReplaceTool.cs`

**Why:** Expose TextReplaceTool via MCP protocol.

**What:**
1. Create `McpTextReplaceTool(McpSettings settings) : TextReplaceTool(settings.VaultPath, settings.AllowedExtensions)` with:
   - `[McpServerToolType]` class attribute
   - `[McpServerTool(Name = Name)]` and `[Description(Description)]` method attributes
   - `McpRun` method accepting: filePath, oldText, newText, occurrence (default "first"), expectedHash (nullable)
   - Each parameter gets a `[Description]` attribute per design doc
   - Returns `ToolResponse.Create(Run(...))`

**Tests:** No unit tests needed for MCP wrapper (thin delegation layer, covered by integration).

---

### Step 4: Remove deprecated targets and operations from TextPatchTool

**Files:**
- Modify `Domain/Tools/Text/TextPatchTool.cs`
- Modify `Tests/Unit/Domain/Text/TextPatchToolTests.cs`

**Why:** Simplify TextPatch by removing targets/operations that are either replaced by TextReplace or confusing.

**What:**
1. Remove from `ResolveTarget`:
   - `text` target handling (replaced by TextReplace)
   - `pattern` target handling (error-prone for edits, keep in TextInspect only)
   - `afterHeading` target handling (replaced by appendToSection in next step)
   - `section` (INI-style) target handling (rarely used)
2. Remove from operation switch:
   - `replaceLines` operation (redundant with `replace` + `lines` target)
3. Remove private methods:
   - `FindTextTarget`
   - `FindPatternTarget`
   - `FindSectionTarget`
   - `ApplyReplaceLines`
4. Update `Description` constant to match the design doc's updated description
5. Update `ValidateOperation` to only accept `replace`, `insert`, `delete`
6. Update the error message in `ResolveTarget` to list only remaining targets
7. Update `TextPatchTool` to inherit from `TextToolBase`

**Tests:**
- Remove tests: `Run_ReplaceText_ReplacesFirstOccurrence`, `Run_ReplaceLines_ReplacesLineRange`, `Run_ReplaceWithPattern_MatchesRegex`, `Run_TextNotFound_ThrowsWithSuggestion`, `Run_InsertAfterHeading_InsertsContent`
- Add test: `Run_TextTarget_ThrowsArgumentException` (verify removed)
- Add test: `Run_PatternTarget_ThrowsArgumentException` (verify removed)
- Add test: `Run_SectionTarget_ThrowsArgumentException` (verify removed)
- Add test: `Run_ReplaceLinesOperation_ThrowsArgumentException` (verify removed)
- Existing passing tests for lines, heading, beforeHeading, codeBlock, delete, replace must still pass

---

### Step 5: Add appendToSection target to TextPatchTool

**Files:**
- Modify `Domain/Tools/Text/TextPatchTool.cs`

**Why:** Replace afterHeading with a target that inserts at the END of a section rather than immediately after the heading line.

**What:**
1. Add `appendToSection` handling in `ResolveTarget` (before the `beforeHeading` block):
   - Parse markdown structure
   - Find heading index using a helper `FindHeadingIndex(structure, heading)` that searches headings by normalized text match (same logic as `FindHeadingTarget` but returns index into headings list)
   - Call `MarkdownParser.FindHeadingEnd(structure.Headings, headingIndex, lines.Count)` to get end line
   - Return `(endLine, endLine, null)` so insert places content at end of section
2. Add private helper `FindHeadingIndex(MarkdownStructure structure, string heading)` that returns the index in the headings list

**Tests:**
- `Run_AppendToSection_InsertsAtEndOfSection` - Create a markdown file with `# Intro\nIntro text\n## Setup\nSetup text\n## Config\nConfig text`, insert with appendToSection="## Setup", verify content appears between "Setup text" and "## Config"
- `Run_AppendToSection_LastSection_InsertsAtEndOfFile` - Section at end of file, content appended before EOF
- `Run_AppendToSection_NonMarkdown_Throws`
- `Run_AppendToSection_HeadingNotFound_Throws`

---

### Step 6: Add expectedHash parameter and context window to TextPatchTool

**Files:**
- Modify `Domain/Tools/Text/TextPatchTool.cs`
- Modify `McpServerText/McpTools/McpTextPatchTool.cs`

**Why:** Enable staleness detection and give the agent updated line context after edits.

**What:**
1. Add `string? expectedHash = null` parameter to `TextPatchTool.Run`
2. After reading lines, call `ValidateExpectedHash(lines, expectedHash)` if provided
3. After writing the file atomically, add context window:
   - Re-read the file: `var updatedLines = File.ReadAllLines(fullPath)`
   - Compute new end line: `var newEndLine = startLine + (updatedLines.Length - originalLineCount) + (endLine - startLine)`
   - Build `contextBefore` JsonArray: 3 lines before startLine
   - Build `contextAfter` JsonArray: 3 lines after newEndLine
   - Add `result["context"]` with beforeLines and afterLines
4. Add `result["fileHash"] = ComputeFileHash(updatedLines)` to response
5. Update `McpTextPatchTool.McpRun` to accept `string? expectedHash = null` parameter with `[Description]` and pass to Run

**Tests:**
- `Run_ReturnsContextWindow_AfterEdit` - Verify context.beforeLines and context.afterLines present
- `Run_ReturnsFileHash_AfterEdit`
- `Run_ExpectedHashMatches_ProceedsWithEdit`
- `Run_ExpectedHashMismatches_ThrowsBeforeEditing`
- `Run_ContextWindow_AtFileStart_HasNoBeforeLines`
- `Run_ContextWindow_AtFileEnd_HasNoAfterLines`

---

### Step 7: Add fileHash to TextInspect structure mode response

**Files:**
- Modify `Domain/Tools/Text/TextInspectTool.cs`

**Why:** Give agents a hash to pass to subsequent edit calls for staleness detection.

**What:**
1. Update `TextInspectTool` to inherit from `TextToolBase`
2. In `InspectStructure`, after building the result, add: `result["fileHash"] = ComputeFileHash(lines)`

**Tests:**
- `Run_StructureMode_ReturnsFileHash` - Verify fileHash is present and is a 16-char hex string
- `Run_StructureMode_FileHash_ChangesWhenContentChanges`

---

### Step 8: Update KnowledgeBasePrompt with editing best practices

**Files:**
- Modify `Domain/Prompts/KnowledgeBasePrompt.cs`

**Why:** Guide the agent to use the right tool for each editing scenario.

**What:**
1. In `AgentSystemPrompt`, update the `### Available Tools` section to add `TextReplace` tool description
2. Replace the `### Targeting Best Practices` section with the new `### Editing Best Practices` section from the design doc, including:
   - TextReplace as default for inline changes
   - TextPatch with appendToSection/beforeHeading for inserting
   - TextPatch with heading/codeBlock for replacing sections
   - TextPatch with lines as last resort
   - expectedHash workflow for multi-edit scenarios
3. Remove references to `afterHeading`, `text` target, `pattern` target, `section` target, and `replaceLines` operation
4. Update the `### Workflow Patterns` / `Editing Documents` section to mention TextReplace first

**Tests:** No tests needed (pure prompt text changes).

---

## Dependency Order

```
Step 1 (TextToolBase)
  --> Step 2 (TextReplaceTool) --> Step 3 (McpTextReplaceTool)
  --> Step 4 (Remove deprecated) --> Step 5 (appendToSection)
  --> Step 6 (hash + context in TextPatch)
  --> Step 7 (hash in TextInspect)
Step 8 (prompts) -- independent, do last
```

Steps 2, 4, and 7 can be done in parallel after Step 1. Step 5 depends on Step 4. Step 6 depends on Step 1. Step 3 depends on Step 2. Step 8 is independent but should be done last.

## Verification

After all steps, run the full test suite:
```bash
dotnet test Tests/
```

Verify:
- All new tests pass
- All existing unmodified tests pass
- No compilation errors across the solution
