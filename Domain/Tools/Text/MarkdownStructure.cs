namespace Domain.Tools.Text;

public record MarkdownHeading(int Level, string Text, int Line);

public record MarkdownCodeBlock(string? Language, int StartLine, int EndLine);

public record MarkdownFrontmatter(int StartLine, int EndLine, IReadOnlyList<string> Keys);

public record MarkdownAnchor(string Id, int Line);

public record TextSection(string Marker, int Line);

public record MarkdownStructure
{
    public MarkdownFrontmatter? Frontmatter { get; init; }
    public required IReadOnlyList<MarkdownHeading> Headings { get; init; }
    public required IReadOnlyList<MarkdownCodeBlock> CodeBlocks { get; init; }
    public required IReadOnlyList<MarkdownAnchor> Anchors { get; init; }
}

public record TextStructure
{
    public required IReadOnlyList<TextSection> Sections { get; init; }
    public required IReadOnlyList<int> BlankLineGroups { get; init; }
}