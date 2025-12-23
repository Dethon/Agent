using System.Text.RegularExpressions;

namespace Domain.Tools.Text;

public static partial class MarkdownParser
{
    public static MarkdownStructure Parse(string[] lines)
    {
        var indexed = lines.Select((line, i) => (Line: line, Number: i + 1)).ToList();
        var frontMatter = ParseFrontMatter(indexed);
        var contentStart = frontMatter?.EndLine ?? 0;
        var contentLines = indexed.Skip(contentStart).ToList();
        var codeBlockRanges = ParseCodeBlocks(contentLines).ToList();

        var nonCodeLines = contentLines
            .Where(l => !IsInsideCodeBlock(l.Number, codeBlockRanges))
            .ToList();

        return new MarkdownStructure
        {
            Frontmatter = frontMatter,
            Headings = ParseHeadings(nonCodeLines).ToList(),
            CodeBlocks = codeBlockRanges,
            Anchors = ParseAnchors(nonCodeLines).ToList()
        };
    }

    public static TextStructure ParsePlainText(string[] lines)
    {
        return new TextStructure
        {
            Sections = ParseSections(lines).ToList(),
            BlankLineGroups = ParseBlankLineGroups(lines).ToList()
        };
    }

    public static int FindHeadingEnd(IReadOnlyList<MarkdownHeading> headings, int headingIndex, int totalLines)
    {
        return headings
            .Skip(headingIndex + 1)
            .FirstOrDefault(h => h.Level <= headings[headingIndex].Level)
            ?.Line - 1 ?? totalLines;
    }

    public static int FindSectionEnd(IReadOnlyList<TextSection> sections, int sectionIndex, int totalLines)
    {
        return sectionIndex + 1 < sections.Count
            ? sections[sectionIndex + 1].Line - 1
            : totalLines;
    }

    private static MarkdownFrontmatter? ParseFrontMatter(IReadOnlyList<(string Line, int Number)> lines)
    {
        if (lines.Count == 0 || lines[0].Line != "---")
        {
            return null;
        }

        var endIndex = lines
            .Skip(1)
            .Select((l, i) => (l.Line, Index: i + 1))
            .FirstOrDefault(x => x.Line == "---")
            .Index;

        if (endIndex == 0)
        {
            return null;
        }

        var keys = lines
            .Skip(1)
            .Take(endIndex - 1)
            .Select(l => l.Line.IndexOf(':'))
            .Where(colonIdx => colonIdx > 0)
            .Select((colonIdx, i) => lines[i + 1].Line[..colonIdx].Trim())
            .ToList();

        return new MarkdownFrontmatter(1, lines[endIndex].Number, keys);
    }

    private static IEnumerable<MarkdownCodeBlock> ParseCodeBlocks(IEnumerable<(string Line, int Number)> lines)
    {
        int? blockStart = null;
        string? language = null;

        foreach (var (line, number) in lines)
        {
            if (!line.StartsWith("```"))
            {
                continue;
            }

            if (blockStart is null)
            {
                blockStart = number;
                language = ExtractCodeLanguage(line);
            }
            else
            {
                yield return new MarkdownCodeBlock(language, blockStart.Value, number);
                blockStart = null;
                language = null;
            }
        }
    }

    private static string? ExtractCodeLanguage(string fenceLine)
    {
        return fenceLine.Length > 3 ? fenceLine[3..].Trim().NullIfEmpty() : null;
    }

    private static bool IsInsideCodeBlock(int lineNumber, IEnumerable<MarkdownCodeBlock> codeBlocks)
    {
        return codeBlocks.Any(cb => lineNumber >= cb.StartLine && lineNumber <= cb.EndLine);
    }

    private static IEnumerable<MarkdownHeading> ParseHeadings(IEnumerable<(string Line, int Number)> lines)
    {
        return lines
            .Select(l => (Match: HeadingRegex().Match(l.Line), l.Number))
            .Where(x => x.Match.Success)
            .Select(x => new MarkdownHeading(
                x.Match.Groups[1].Value.Length,
                x.Match.Groups[2].Value.Trim(),
                x.Number));
    }

    private static IEnumerable<MarkdownAnchor> ParseAnchors(IEnumerable<(string Line, int Number)> lines)
    {
        return lines.SelectMany(l => ExtractAnchorsFromLine(l.Line, l.Number));
    }

    private static IEnumerable<MarkdownAnchor> ExtractAnchorsFromLine(string line, int lineNumber)
    {
        foreach (Match match in AnchorRegex().Matches(line))
        {
            yield return new MarkdownAnchor(match.Groups[1].Value, lineNumber);
        }

        var hashMatch = HashAnchorRegex().Match(line);
        if (hashMatch.Success)
        {
            yield return new MarkdownAnchor(hashMatch.Groups[1].Value, lineNumber);
        }
    }

    private static IEnumerable<TextSection> ParseSections(string[] lines)
    {
        return lines
            .Select((line, i) => (Line: line, Number: i + 1))
            .Where(l => l.Line.StartsWith('[') && l.Line.EndsWith(']'))
            .Select(l => new TextSection(l.Line, l.Number));
    }

    private static IEnumerable<int> ParseBlankLineGroups(string[] lines)
    {
        return lines
            .Select((line, i) => (Line: line, Index: i, Number: i + 1))
            .Where(l => string.IsNullOrWhiteSpace(l.Line))
            .Where(l => l.Index == 0 || !string.IsNullOrWhiteSpace(lines[l.Index - 1]))
            .Select(l => l.Number);
    }

    private static string? NullIfEmpty(this string s)
    {
        return string.IsNullOrEmpty(s) ? null : s;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex("""id=["']([^"']+)["']""")]
    private static partial Regex AnchorRegex();

    [GeneratedRegex(@"\{#([^}]+)\}")]
    private static partial Regex HashAnchorRegex();
}