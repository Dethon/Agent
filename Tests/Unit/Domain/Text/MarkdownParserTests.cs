using Domain.Tools.Text;
using Shouldly;

namespace Tests.Unit.Domain.Text;

public class MarkdownParserTests
{
    [Fact]
    public void Parse_EmptyFile_ReturnsEmptyStructure()
    {
        var result = MarkdownParser.Parse([]);

        result.Frontmatter.ShouldBeNull();
        result.Headings.ShouldBeEmpty();
        result.CodeBlocks.ShouldBeEmpty();
        result.Anchors.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_WithFrontmatter_ExtractsFrontmatterKeys()
    {
        var lines = new[]
        {
            "---",
            "title: My Document",
            "date: 2024-01-01",
            "tags: [one, two]",
            "---",
            "# Content"
        };

        var result = MarkdownParser.Parse(lines);

        result.Frontmatter.ShouldNotBeNull();
        result.Frontmatter.StartLine.ShouldBe(1);
        result.Frontmatter.EndLine.ShouldBe(5);
        result.Frontmatter.Keys.ShouldBe(["title", "date", "tags"]);
    }

    [Fact]
    public void Parse_WithHeadings_ExtractsAllLevels()
    {
        var lines = new[]
        {
            "# Heading 1",
            "Some text",
            "## Heading 2",
            "More text",
            "### Heading 3",
            "#### Heading 4"
        };

        var result = MarkdownParser.Parse(lines);

        result.Headings.Count.ShouldBe(4);
        result.Headings[0].ShouldBe(new MarkdownHeading(1, "Heading 1", 1));
        result.Headings[1].ShouldBe(new MarkdownHeading(2, "Heading 2", 3));
        result.Headings[2].ShouldBe(new MarkdownHeading(3, "Heading 3", 5));
        result.Headings[3].ShouldBe(new MarkdownHeading(4, "Heading 4", 6));
    }

    [Fact]
    public void Parse_WithCodeBlocks_ExtractsLanguageAndLines()
    {
        var lines = new[]
        {
            "# Code Examples",
            "```csharp",
            "var x = 1;",
            "```",
            "Some text",
            "```",
            "plain code",
            "```"
        };

        var result = MarkdownParser.Parse(lines);

        result.CodeBlocks.Count.ShouldBe(2);
        result.CodeBlocks[0].ShouldBe(new MarkdownCodeBlock("csharp", 2, 4));
        result.CodeBlocks[1].ShouldBe(new MarkdownCodeBlock(null, 6, 8));
    }

    [Fact]
    public void Parse_HeadingsInsideCodeBlocks_AreIgnored()
    {
        var lines = new[]
        {
            "# Real Heading",
            "```markdown",
            "# Fake Heading Inside Code",
            "## Another Fake",
            "```",
            "## Real Heading 2"
        };

        var result = MarkdownParser.Parse(lines);

        result.Headings.Count.ShouldBe(2);
        result.Headings[0].Text.ShouldBe("Real Heading");
        result.Headings[1].Text.ShouldBe("Real Heading 2");
    }

    [Fact]
    public void Parse_WithHtmlAnchors_ExtractsIds()
    {
        var lines = new[]
        {
            "# Heading",
            "<a id=\"my-anchor\"></a>",
            "<div id='another-anchor'>Content</div>",
            "Normal text"
        };

        var result = MarkdownParser.Parse(lines);

        result.Anchors.Count.ShouldBe(2);
        result.Anchors[0].ShouldBe(new MarkdownAnchor("my-anchor", 2));
        result.Anchors[1].ShouldBe(new MarkdownAnchor("another-anchor", 3));
    }

    [Fact]
    public void Parse_WithHashAnchors_ExtractsIds()
    {
        var lines = new[]
        {
            "# Heading {#custom-id}",
            "Some text"
        };

        var result = MarkdownParser.Parse(lines);

        result.Anchors.Count.ShouldBe(1);
        result.Anchors[0].ShouldBe(new MarkdownAnchor("custom-id", 1));
    }

    [Fact]
    public void ParsePlainText_WithIniSections_ExtractsSections()
    {
        var lines = new[]
        {
            "; comment",
            "[database]",
            "host=localhost",
            "port=5432",
            "",
            "[cache]",
            "enabled=true"
        };

        var result = MarkdownParser.ParsePlainText(lines);

        result.Sections.Count.ShouldBe(2);
        result.Sections[0].ShouldBe(new TextSection("[database]", 2));
        result.Sections[1].ShouldBe(new TextSection("[cache]", 6));
    }

    [Fact]
    public void ParsePlainText_TracksBlankLineGroups()
    {
        var lines = new[]
        {
            "line 1",
            "",
            "line 3",
            "line 4",
            "",
            "",
            "line 7"
        };

        var result = MarkdownParser.ParsePlainText(lines);

        result.BlankLineGroups.ShouldBe([2, 5]);
    }

    [Fact]
    public void FindHeadingEnd_ReturnsNextSameLevelHeading()
    {
        var headings = new List<MarkdownHeading>
        {
            new(2, "Section 1", 10),
            new(3, "Subsection", 15),
            new(2, "Section 2", 25),
            new(2, "Section 3", 40)
        };

        var end = MarkdownParser.FindHeadingEnd(headings, 0, 100);

        end.ShouldBe(24); // Line before Section 2
    }

    [Fact]
    public void FindHeadingEnd_LastHeading_ReturnsTotalLines()
    {
        var headings = new List<MarkdownHeading>
        {
            new(2, "Section 1", 10),
            new(2, "Section 2", 25)
        };

        var end = MarkdownParser.FindHeadingEnd(headings, 1, 100);

        end.ShouldBe(100);
    }

    [Fact]
    public void FindSectionEnd_ReturnsNextSectionLine()
    {
        var sections = new List<TextSection>
        {
            new("[database]", 5),
            new("[cache]", 20),
            new("[logging]", 35)
        };

        var end = MarkdownParser.FindSectionEnd(sections, 0, 100);

        end.ShouldBe(19);
    }

    [Fact]
    public void FindSectionEnd_LastSection_ReturnsTotalLines()
    {
        var sections = new List<TextSection>
        {
            new("[database]", 5),
            new("[cache]", 20)
        };

        var end = MarkdownParser.FindSectionEnd(sections, 1, 100);

        end.ShouldBe(100);
    }
}