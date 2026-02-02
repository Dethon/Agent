using System.Security.Cryptography;
using System.Text;
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

    // --- Positional targeting tests (from TextPatchToolTests) ---

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
        newContent.ShouldContain("```csharp");
    }

    [Fact]
    public void Run_HeadingNotFound_ThrowsWithSimilar()
    {
        var content = "# Introduction\n## Installation\n## Configuration";
        var filePath = CreateTestFile("doc.md", content);

        var target = new JsonObject { ["heading"] = "## Instalation" };

        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "replace", target, "## Setup"));
        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public void Run_MissingContentForReplace_ThrowsException()
    {
        var filePath = CreateTestFile("test.md", "Content");

        var target = new JsonObject { ["lines"] = new JsonObject { ["start"] = 1, ["end"] = 1 } };

        Should.Throw<ArgumentException>(() => _tool.TestRun(filePath, "replace", target));
    }

    [Fact]
    public void Run_PathOutsideVault_ThrowsException()
    {
        var target = new JsonObject { ["lines"] = new JsonObject { ["start"] = 1, ["end"] = 1 } };

        Should.Throw<UnauthorizedAccessException>(() =>
            _tool.TestRun("/etc/passwd", "replace", target, "new"));
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

        newContent.ShouldContain("Setup text");
        newContent.ShouldContain("New content");
        newContent.ShouldContain("## Config");

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

    [Fact]
    public void Run_InvalidOperation_ThrowsException()
    {
        var filePath = CreateTestFile("test.md", "Line 1\nLine 2\nLine 3");

        var target = new JsonObject { ["lines"] = new JsonObject { ["start"] = 1, ["end"] = 2 } };

        var ex = Should.Throw<ArgumentException>(() =>
            _tool.TestRun(filePath, "replaceLines", target, "New content"));
        ex.Message.ShouldContain("replaceLines");
    }

    // --- Text target tests (from TextReplaceToolTests) ---

    [Fact]
    public void Run_TextTarget_SingleOccurrence_ReplacesText()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var target = new JsonObject { ["text"] = "World" };
        var result = _tool.TestRun(filePath, "replace", target, "Universe");

        result["status"]!.ToString().ShouldBe("success");
        result["occurrencesFound"]!.GetValue<int>().ShouldBe(1);
        result["occurrencesReplaced"]!.GetValue<int>().ShouldBe(1);
        var content = File.ReadAllText(filePath);
        content.ShouldBe("Hello Universe");
    }

    [Fact]
    public void Run_TextTarget_MultipleOccurrences_ReplacesFirst_ByDefault()
    {
        var filePath = CreateTestFile("test.txt", "foo bar foo baz foo");

        var target = new JsonObject { ["text"] = "foo" };
        var result = _tool.TestRun(filePath, "replace", target, "FOO");

        result["status"]!.ToString().ShouldBe("success");
        result["occurrencesFound"]!.GetValue<int>().ShouldBe(3);
        result["occurrencesReplaced"]!.GetValue<int>().ShouldBe(1);
        var content = File.ReadAllText(filePath);
        content.ShouldBe("FOO bar foo baz foo");
        result["note"]!.ToString().ShouldContain("2 other occurrence(s) remain");
    }

    [Fact]
    public void Run_TextTarget_ReplacesLast()
    {
        var filePath = CreateTestFile("test.txt", "foo bar foo baz foo");

        var target = new JsonObject { ["text"] = "foo" };
        var result = _tool.TestRun(filePath, "replace", target, "FOO", occurrence: "last");

        result["status"]!.ToString().ShouldBe("success");
        var content = File.ReadAllText(filePath);
        content.ShouldBe("foo bar foo baz FOO");
    }

    [Fact]
    public void Run_TextTarget_ReplacesAll()
    {
        var filePath = CreateTestFile("test.txt", "foo bar foo baz foo");

        var target = new JsonObject { ["text"] = "foo" };
        var result = _tool.TestRun(filePath, "replace", target, "FOO", occurrence: "all");

        result["status"]!.ToString().ShouldBe("success");
        result["occurrencesReplaced"]!.GetValue<int>().ShouldBe(3);
        var content = File.ReadAllText(filePath);
        content.ShouldBe("FOO bar FOO baz FOO");
        result.AsObject().ContainsKey("note").ShouldBeFalse();
    }

    [Fact]
    public void Run_TextTarget_ReplacesNth()
    {
        var filePath = CreateTestFile("test.txt", "foo bar foo baz foo");

        var target = new JsonObject { ["text"] = "foo" };
        var result = _tool.TestRun(filePath, "replace", target, "FOO", occurrence: "2");

        result["status"]!.ToString().ShouldBe("success");
        var content = File.ReadAllText(filePath);
        content.ShouldBe("foo bar FOO baz foo");
    }

    [Fact]
    public void Run_TextTarget_NthExceedsTotal_Throws()
    {
        var filePath = CreateTestFile("test.txt", "foo bar");

        var target = new JsonObject { ["text"] = "foo" };

        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "replace", target, "FOO", occurrence: "5"));
        ex.Message.ShouldContain("Occurrence 5 requested but only 1 found");
    }

    [Fact]
    public void Run_TextTarget_NotFound_ThrowsWithMessage()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var target = new JsonObject { ["text"] = "Missing" };

        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "replace", target, "X"));
        ex.Message.ShouldContain("Text 'Missing' not found");
    }

    [Fact]
    public void Run_TextTarget_CaseInsensitiveMatch_ThrowsWithSuggestion()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var target = new JsonObject { ["text"] = "hello world" };

        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "replace", target, "X"));
        ex.Message.ShouldContain("Did you mean 'Hello World'");
    }

    [Fact]
    public void Run_TextTarget_MultilineOldText_ReplacesAcrossLines()
    {
        var filePath = CreateTestFile("test.txt", "Line 1\nLine 2\nLine 3\nLine 4");

        var target = new JsonObject { ["text"] = "Line 2\nLine 3" };
        var result = _tool.TestRun(filePath, "replace", target, "Replacement");

        result["status"]!.ToString().ShouldBe("success");
        var content = File.ReadAllText(filePath);
        content.ShouldBe("Line 1\nReplacement\nLine 4");
    }

    [Fact]
    public void Run_TextTarget_ReturnsContextLines()
    {
        var filePath = CreateTestFile("test.txt", "Line 1\nLine 2\nLine 3\nTarget\nLine 5\nLine 6\nLine 7");

        var target = new JsonObject { ["text"] = "Target" };
        var result = _tool.TestRun(filePath, "replace", target, "Replaced");

        result["context"]!.AsArray().Count.ShouldBeGreaterThan(0);
        var contextStr = string.Join("\n", result["context"]!.AsArray().Select(x => x!.ToString()));
        contextStr.ShouldContain("Line 3");
        contextStr.ShouldContain("Line 5");
    }

    [Fact]
    public void Run_TextTarget_ReturnsFileHash()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var target = new JsonObject { ["text"] = "World" };
        var result = _tool.TestRun(filePath, "replace", target, "Universe");

        result["fileHash"]!.ToString().ShouldNotBeNullOrEmpty();
        result["fileHash"]!.ToString().Length.ShouldBe(16);
    }

    [Fact]
    public void Run_TextTarget_ExpectedHashMatches_Succeeds()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");
        var lines = File.ReadAllLines(filePath);
        var hash = ComputeTestHash(lines);

        var target = new JsonObject { ["text"] = "World" };
        var result = _tool.TestRun(filePath, "replace", target, "Universe", expectedHash: hash);

        result["status"]!.ToString().ShouldBe("success");
    }

    [Fact]
    public void Run_TextTarget_ExpectedHashMismatches_Throws()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var target = new JsonObject { ["text"] = "World" };

        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "replace", target, "Universe", expectedHash: "wrong0hash0here"));
        ex.Message.ShouldContain("File hash mismatch");
    }

    [Fact]
    public void Run_TextTarget_InsertOperation_Throws()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var target = new JsonObject { ["text"] = "World" };

        var ex = Should.Throw<ArgumentException>(() =>
            _tool.TestRun(filePath, "insert", target, "New text"));
        ex.Message.ShouldContain("only supports 'replace'");
    }

    [Fact]
    public void Run_TextTarget_DeleteOperation_Throws()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var target = new JsonObject { ["text"] = "World" };

        var ex = Should.Throw<ArgumentException>(() =>
            _tool.TestRun(filePath, "delete", target, ""));
        ex.Message.ShouldContain("only supports 'replace'");
    }

    [Fact]
    public void Run_TextTarget_DisallowedExtension_Throws()
    {
        var filePath = CreateTestFile("test.exe", "content");

        var target = new JsonObject { ["text"] = "old" };

        Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "replace", target, "new"));
    }

    [Fact]
    public void Run_InvalidTarget_ThrowsWithTextInList()
    {
        var filePath = CreateTestFile("test.md", "Some content");

        var target = new JsonObject { ["unknown"] = "value" };

        var ex = Should.Throw<ArgumentException>(() =>
            _tool.TestRun(filePath, "replace", target, "new"));
        ex.Message.ShouldContain("text");
        ex.Message.ShouldContain("lines");
        ex.Message.ShouldContain("heading");
    }

    private string CreateTestFile(string name, string content)
    {
        var path = Path.Combine(_testDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string ComputeTestHash(string[] lines)
    {
        var content = string.Join("\n", lines);
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private class TestableTextEditTool(string vaultPath, string[] allowedExtensions)
        : TextEditTool(vaultPath, allowedExtensions)
    {
        public JsonNode TestRun(string filePath, string operation, JsonObject target, string? content = null,
            string? occurrence = null, bool preserveIndent = true, string? expectedHash = null)
        {
            return Run(filePath, operation, target, content, occurrence, preserveIndent, expectedHash);
        }
    }
}
