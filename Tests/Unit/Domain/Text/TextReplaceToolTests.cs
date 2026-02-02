using System.Text.Json.Nodes;
using Domain.Tools.Text;
using Shouldly;

namespace Tests.Unit.Domain.Text;

public class TextReplaceToolTests : IDisposable
{
    private readonly string _testDir;
    private readonly TestableTextReplaceTool _tool;

    public TextReplaceToolTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"text-replace-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
        _tool = new TestableTextReplaceTool(_testDir, [".md", ".txt"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    [Fact]
    public void Run_SingleOccurrence_ReplacesText()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var result = _tool.TestRun(filePath, "World", "Universe");

        result["status"]!.ToString().ShouldBe("success");
        result["occurrencesFound"]!.GetValue<int>().ShouldBe(1);
        result["occurrencesReplaced"]!.GetValue<int>().ShouldBe(1);
        var content = File.ReadAllText(filePath);
        content.ShouldBe("Hello Universe");
    }

    [Fact]
    public void Run_MultipleOccurrences_ReplacesFirst_ByDefault()
    {
        var filePath = CreateTestFile("test.txt", "foo bar foo baz foo");

        var result = _tool.TestRun(filePath, "foo", "FOO");

        result["status"]!.ToString().ShouldBe("success");
        result["occurrencesFound"]!.GetValue<int>().ShouldBe(3);
        result["occurrencesReplaced"]!.GetValue<int>().ShouldBe(1);
        var content = File.ReadAllText(filePath);
        content.ShouldBe("FOO bar foo baz foo");
        result["note"]!.ToString().ShouldContain("2 other occurrence(s) remain");
    }

    [Fact]
    public void Run_MultipleOccurrences_ReplacesLast()
    {
        var filePath = CreateTestFile("test.txt", "foo bar foo baz foo");

        var result = _tool.TestRun(filePath, "foo", "FOO", "last");

        result["status"]!.ToString().ShouldBe("success");
        result["occurrencesFound"]!.GetValue<int>().ShouldBe(3);
        result["occurrencesReplaced"]!.GetValue<int>().ShouldBe(1);
        var content = File.ReadAllText(filePath);
        content.ShouldBe("foo bar foo baz FOO");
    }

    [Fact]
    public void Run_MultipleOccurrences_ReplacesAll()
    {
        var filePath = CreateTestFile("test.txt", "foo bar foo baz foo");

        var result = _tool.TestRun(filePath, "foo", "FOO", "all");

        result["status"]!.ToString().ShouldBe("success");
        result["occurrencesFound"]!.GetValue<int>().ShouldBe(3);
        result["occurrencesReplaced"]!.GetValue<int>().ShouldBe(3);
        var content = File.ReadAllText(filePath);
        content.ShouldBe("FOO bar FOO baz FOO");
        result.AsObject().ContainsKey("note").ShouldBeFalse();
    }

    [Fact]
    public void Run_MultipleOccurrences_ReplacesNth()
    {
        var filePath = CreateTestFile("test.txt", "foo bar foo baz foo");

        var result = _tool.TestRun(filePath, "foo", "FOO", "2");

        result["status"]!.ToString().ShouldBe("success");
        result["occurrencesFound"]!.GetValue<int>().ShouldBe(3);
        result["occurrencesReplaced"]!.GetValue<int>().ShouldBe(1);
        var content = File.ReadAllText(filePath);
        content.ShouldBe("foo bar FOO baz foo");
    }

    [Fact]
    public void Run_NthOccurrence_ExceedsTotal_Throws()
    {
        var filePath = CreateTestFile("test.txt", "foo bar");

        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "foo", "FOO", "5"));
        ex.Message.ShouldContain("Occurrence 5 requested but only 1 found");
    }

    [Fact]
    public void Run_TextNotFound_ThrowsWithMessage()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "Missing", "X"));
        ex.Message.ShouldContain("Text 'Missing' not found");
    }

    [Fact]
    public void Run_TextNotFound_CaseInsensitiveMatch_ThrowsWithSuggestion()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "hello world", "X"));
        ex.Message.ShouldContain("Did you mean 'Hello World'");
    }

    [Fact]
    public void Run_MultilineOldText_ReplacesAcrossLines()
    {
        var filePath = CreateTestFile("test.txt", "Line 1\nLine 2\nLine 3\nLine 4");

        var result = _tool.TestRun(filePath, "Line 2\nLine 3", "Replacement");

        result["status"]!.ToString().ShouldBe("success");
        var content = File.ReadAllText(filePath);
        content.ShouldBe("Line 1\nReplacement\nLine 4");
    }

    [Fact]
    public void Run_ReturnsContextLines()
    {
        var filePath = CreateTestFile("test.txt", "Line 1\nLine 2\nLine 3\nTarget\nLine 5\nLine 6\nLine 7");

        var result = _tool.TestRun(filePath, "Target", "Replaced");

        result["context"]!.AsArray().Count.ShouldBeGreaterThan(0);
        var contextStr = string.Join("\n", result["context"]!.AsArray().Select(x => x!.ToString()));
        contextStr.ShouldContain("Line 3");
        contextStr.ShouldContain("Line 5");
    }

    [Fact]
    public void Run_ReturnsFileHash()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var result = _tool.TestRun(filePath, "World", "Universe");

        result["fileHash"]!.ToString().ShouldNotBeNullOrEmpty();
        result["fileHash"]!.ToString().Length.ShouldBe(16);
    }

    [Fact]
    public void Run_ExpectedHashMatches_Succeeds()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");
        var lines = File.ReadAllLines(filePath);
        var hash = ComputeTestHash(lines);

        var result = _tool.TestRun(filePath, "World", "Universe", expectedHash: hash);

        result["status"]!.ToString().ShouldBe("success");
    }

    [Fact]
    public void Run_ExpectedHashMismatches_Throws()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "World", "Universe", expectedHash: "wrong0hash0here"));
        ex.Message.ShouldContain("File hash mismatch");
    }

    [Fact]
    public void Run_FirstOccurrence_NoteIncludesOtherLocations()
    {
        var filePath = CreateTestFile("test.txt", "foo\nbar\nfoo\nbaz\nfoo");

        var result = _tool.TestRun(filePath, "foo", "FOO", "first");

        result["note"]!.ToString().ShouldContain("other occurrence(s) remain");
    }

    [Fact]
    public void Run_PathOutsideVault_Throws()
    {
        Should.Throw<UnauthorizedAccessException>(() =>
            _tool.TestRun("/etc/passwd", "old", "new"));
    }

    [Fact]
    public void Run_DisallowedExtension_Throws()
    {
        var filePath = CreateTestFile("test.exe", "content");

        Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "old", "new"));
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
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private class TestableTextReplaceTool(string vaultPath, string[] allowedExtensions)
        : TextReplaceTool(vaultPath, allowedExtensions)
    {
        public JsonNode TestRun(string filePath, string oldText, string newText, string occurrence = "first",
            string? expectedHash = null)
        {
            return Run(filePath, oldText, newText, occurrence, expectedHash);
        }
    }
}
