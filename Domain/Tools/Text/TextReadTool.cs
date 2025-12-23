using System.Text.Json.Nodes;

namespace Domain.Tools.Text;

public class TextReadTool(string vaultPath, string[] allowedExtensions)
{
    protected const string Name = "TextRead";

    protected const string Description = """
                                         Reads a specific section of a text file. Use after TextInspect to read targeted content.

                                         Targeting methods (use ONE):
                                         - lines: { "start": N, "end": M } - Read specific line range
                                         - heading: { "text": "Section Name", "includeChildren": true/false } - Read markdown section
                                         - codeBlock: { "index": N } - Read Nth code block (0-based)
                                         - anchor: "anchor-id" - Read from anchor to next heading
                                         - section: "[marker]" - Read INI-style section
                                         - search: { "query": "text", "contextLines": N } - Read around first match

                                         Best practices:
                                         1. Always use TextInspect first to find line numbers or heading names
                                         2. Prefer heading/section targeting for markdown—more stable than line numbers
                                         3. Use line targeting when you need exact control
                                         4. Large sections may be truncated—use narrower targets

                                         Examples:
                                         - Read lines 50-75: target={ "lines": { "start": 50, "end": 75 } }
                                         - Read Installation section: target={ "heading": { "text": "Installation" } }
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

        if (target.TryGetPropertyValue("heading", out var headingNode) && headingNode is JsonObject headingObj)
        {
            if (!isMarkdown)
            {
                throw new InvalidOperationException("Heading targeting only works with markdown files");
            }

            var text = headingObj["text"]?.GetValue<string>() ?? throw new ArgumentException("heading.text required");
            var includeChildren = headingObj["includeChildren"]?.GetValue<bool>() ?? true;

            return ResolveHeadingTarget(lines, text, includeChildren);
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

        if (target.TryGetPropertyValue("search", out var searchNode) && searchNode is JsonObject searchObj)
        {
            var query = searchObj["query"]?.GetValue<string>() ?? throw new ArgumentException("search.query required");
            var contextLines = searchObj["contextLines"]?.GetValue<int>() ?? 10;
            return ResolveSearchTarget(lines, query, contextLines);
        }

        throw new ArgumentException("Invalid target. Use one of: lines, heading, codeBlock, anchor, section, search");
    }

    private static (int Start, int End) ResolveHeadingTarget(string[] lines, string text, bool includeChildren)
    {
        var structure = MarkdownParser.Parse(lines);

        var headingIndex = structure.Headings
            .Select((h, i) => (h, i))
            .FirstOrDefault(x => x.h.Text.Equals(text, StringComparison.OrdinalIgnoreCase) ||
                                 x.h.Text.Contains(text, StringComparison.OrdinalIgnoreCase));

        if (headingIndex.h is null)
        {
            var similar = structure.Headings
                .Where(h => h.Text.Contains(text.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
                .Take(3)
                .Select(h => h.Text);

            throw new InvalidOperationException(
                $"Heading '{text}' not found. Similar: {string.Join(", ", similar)}. Use TextInspect to list all headings.");
        }

        var startLine = headingIndex.h.Line;
        int endLine;

        if (includeChildren)
        {
            endLine = MarkdownParser.FindHeadingEnd(structure.Headings, headingIndex.i, lines.Length);
        }
        else
        {
            // Find next heading of any level
            var nextHeading = structure.Headings
                .Skip(headingIndex.i + 1)
                .FirstOrDefault();

            endLine = nextHeading?.Line - 1 ?? lines.Length;
        }

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

    private static (int Start, int End) ResolveSearchTarget(string[] lines, string query, int contextLines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                var start = Math.Max(1, i + 1 - contextLines);
                var end = Math.Min(lines.Length, i + 1 + contextLines);
                return (start, end);
            }
        }

        throw new InvalidOperationException(
            $"Text '{query}' not found in file. Use TextInspect with search mode for regex patterns.");
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