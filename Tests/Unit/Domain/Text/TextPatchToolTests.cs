using System.Text.Json.Nodes;
using Domain.Tools.Text;
using Shouldly;

namespace Tests.Unit.Domain.Text;

public class TextPatchToolTests : IDisposable
{
    private readonly string _testDir;
    private readonly TestableTextPatchTool _tool;

    public TextPatchToolTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"text-patch-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
        _tool = new TestableTextPatchTool(_testDir, [".md", ".txt"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

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
        newContent.ShouldContain("```csharp"); // Fence preserved
    }

    [Fact]
    public void Run_HeadingNotFound_ThrowsWithSimilar()
    {
        var content = "# Introduction\n## Installation\n## Configuration";
        var filePath = CreateTestFile("doc.md", content);

        var target = new JsonObject { ["heading"] = "## Instalation" }; // Typo

        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "replace", target, "## Setup"));
        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public void Run_MissingContentForReplace_ThrowsException()
    {
        var filePath = CreateTestFile("test.md", "Content");

        var target = new JsonObject { ["text"] = "Content" };

        Should.Throw<ArgumentException>(() => _tool.TestRun(filePath, "replace", target));
    }

    [Fact]
    public void Run_PathOutsideVault_ThrowsException()
    {
        var target = new JsonObject { ["text"] = "test" };

        Should.Throw<UnauthorizedAccessException>(() =>
            _tool.TestRun("/etc/passwd", "replace", target, "new"));
    }

    [Fact]
    public void Run_TextTarget_ThrowsArgumentException()
    {
        var filePath = CreateTestFile("test.md", "Some content here");

        var target = new JsonObject { ["text"] = "content" };

        var ex = Should.Throw<ArgumentException>(() =>
            _tool.TestRun(filePath, "replace", target, "new content"));
        ex.Message.ShouldContain("text");
    }

    [Fact]
    public void Run_PatternTarget_ThrowsArgumentException()
    {
        var filePath = CreateTestFile("test.md", "Date: 2024-01-15");

        var target = new JsonObject { ["pattern"] = @"\d{4}-\d{2}-\d{2}" };

        var ex = Should.Throw<ArgumentException>(() =>
            _tool.TestRun(filePath, "replace", target, "2025-01-01"));
        ex.Message.ShouldContain("pattern");
    }

    [Fact]
    public void Run_SectionTarget_ThrowsArgumentException()
    {
        var filePath = CreateTestFile("test.txt", "[Section1]\nkey=value");

        var target = new JsonObject { ["section"] = "[Section1]" };

        var ex = Should.Throw<ArgumentException>(() =>
            _tool.TestRun(filePath, "replace", target, "new content"));
        ex.Message.ShouldContain("section");
    }

    [Fact]
    public void Run_ReplaceLinesOperation_ThrowsArgumentException()
    {
        var filePath = CreateTestFile("test.md", "Line 1\nLine 2\nLine 3");

        var target = new JsonObject { ["lines"] = new JsonObject { ["start"] = 1, ["end"] = 2 } };

        var ex = Should.Throw<ArgumentException>(() =>
            _tool.TestRun(filePath, "replaceLines", target, "New content"));
        ex.Message.ShouldContain("replaceLines");
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

        // Verify new content appears between Setup text and Config heading
        newContent.ShouldContain("Setup text");
        newContent.ShouldContain("New content");
        newContent.ShouldContain("## Config");

        // Verify order: Setup text should come before New content, which should come before Config
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

        // Verify new content appears at the end after Setup text
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

    private string CreateTestFile(string name, string content)
    {
        var path = Path.Combine(_testDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private class TestableTextPatchTool(string vaultPath, string[] allowedExtensions)
        : TextPatchTool(vaultPath, allowedExtensions)
    {
        public JsonNode TestRun(string filePath, string operation, JsonObject target, string? content = null)
        {
            return Run(filePath, operation, target, content);
        }
    }
}