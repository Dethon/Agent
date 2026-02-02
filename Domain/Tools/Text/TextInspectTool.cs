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
