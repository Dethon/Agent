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
                                         5. To search within a file, use TextSearch with filePath parameter

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