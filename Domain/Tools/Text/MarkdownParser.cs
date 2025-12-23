using System.Text.RegularExpressions;

namespace Domain.Tools.Text;

public static partial class MarkdownParser
{
    public static MarkdownStructure Parse(string[] lines)
    {
        var headings = new List<MarkdownHeading>();
        var codeBlocks = new List<MarkdownCodeBlock>();
        var anchors = new List<MarkdownAnchor>();
        MarkdownFrontmatter? frontmatter = null;

        var inCodeBlock = false;
        var codeBlockStart = 0;
        string? codeBlockLang = null;
        var inFrontmatter = false;
        var frontmatterStart = 0;
        var frontmatterKeys = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNumber = i + 1;

            // Check for frontmatter (only at start of file)
            if (i == 0 && line == "---")
            {
                inFrontmatter = true;
                frontmatterStart = lineNumber;
                continue;
            }

            if (inFrontmatter)
            {
                if (line == "---")
                {
                    frontmatter = new MarkdownFrontmatter(frontmatterStart, lineNumber, frontmatterKeys);
                    inFrontmatter = false;
                }
                else
                {
                    var colonIndex = line.IndexOf(':');
                    if (colonIndex > 0)
                    {
                        frontmatterKeys.Add(line[..colonIndex].Trim());
                    }
                }

                continue;
            }

            // Check for code blocks
            if (line.StartsWith("```"))
            {
                if (!inCodeBlock)
                {
                    inCodeBlock = true;
                    codeBlockStart = lineNumber;
                    codeBlockLang = line.Length > 3 ? line[3..].Trim() : null;
                    if (string.IsNullOrEmpty(codeBlockLang))
                    {
                        codeBlockLang = null;
                    }
                }
                else
                {
                    codeBlocks.Add(new MarkdownCodeBlock(codeBlockLang, codeBlockStart, lineNumber));
                    inCodeBlock = false;
                    codeBlockLang = null;
                }

                continue;
            }

            if (inCodeBlock)
            {
                continue;
            }

            // Check for headings
            var headingMatch = HeadingRegex().Match(line);
            if (headingMatch.Success)
            {
                var level = headingMatch.Groups[1].Value.Length;
                var text = headingMatch.Groups[2].Value.Trim();
                headings.Add(new MarkdownHeading(level, text, lineNumber));
            }

            // Check for anchors (HTML id attributes or markdown anchor syntax)
            var anchorMatches = AnchorRegex().Matches(line);
            foreach (Match match in anchorMatches)
            {
                anchors.Add(new MarkdownAnchor(match.Groups[1].Value, lineNumber));
            }

            // Also check for {#anchor} syntax
            var hashAnchorMatch = HashAnchorRegex().Match(line);
            if (hashAnchorMatch.Success)
            {
                anchors.Add(new MarkdownAnchor(hashAnchorMatch.Groups[1].Value, lineNumber));
            }
        }

        return new MarkdownStructure
        {
            Frontmatter = frontmatter,
            Headings = headings,
            CodeBlocks = codeBlocks,
            Anchors = anchors
        };
    }

    public static TextStructure ParsePlainText(string[] lines)
    {
        var sections = new List<TextSection>();
        var blankLineGroups = new List<int>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNumber = i + 1;

            // Check for INI-style sections
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                sections.Add(new TextSection(line, lineNumber));
            }

            // Track blank line groups (could indicate logical sections)
            if (string.IsNullOrWhiteSpace(line) &&
                (i == 0 || !string.IsNullOrWhiteSpace(lines[i - 1])))
            {
                blankLineGroups.Add(lineNumber);
            }
        }

        return new TextStructure
        {
            Sections = sections,
            BlankLineGroups = blankLineGroups
        };
    }

    public static int FindHeadingEnd(IReadOnlyList<MarkdownHeading> headings, int headingIndex, int totalLines)
    {
        var heading = headings[headingIndex];

        // Find next heading of same or higher level
        for (var i = headingIndex + 1; i < headings.Count; i++)
        {
            if (headings[i].Level <= heading.Level)
            {
                return headings[i].Line - 1;
            }
        }

        return totalLines;
    }

    public static int FindSectionEnd(IReadOnlyList<TextSection> sections, int sectionIndex, int totalLines)
    {
        if (sectionIndex + 1 < sections.Count)
        {
            return sections[sectionIndex + 1].Line - 1;
        }

        return totalLines;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex("""id=["']([^"']+)["']""")]
    private static partial Regex AnchorRegex();

    [GeneratedRegex(@"\{#([^}]+)\}")]
    private static partial Regex HashAnchorRegex();
}