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

    [Fact]
    public void Run_SingleOccurrence_ReplacesText()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var result = _tool.TestRun(filePath, "World", "Universe");

        result["status"]!.ToString().ShouldBe("success");
        result["occurrencesReplaced"]!.GetValue<int>().ShouldBe(1);
        File.ReadAllText(filePath).ShouldBe("Hello Universe");
    }

    [Fact]
    public void Run_MultipleOccurrences_ReplaceAllFalse_Throws()
    {
        var filePath = CreateTestFile("test.txt", "foo bar foo baz foo");

        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "foo", "FOO"));
        ex.Message.ShouldContain("3 occurrences");
        ex.Message.ShouldContain("disambiguate");
        File.ReadAllText(filePath).ShouldBe("foo bar foo baz foo");
    }

    [Fact]
    public void Run_MultipleOccurrences_ReplaceAllTrue_ReplacesAll()
    {
        var filePath = CreateTestFile("test.txt", "foo bar foo baz foo");

        var result = _tool.TestRun(filePath, "foo", "FOO", replaceAll: true);

        result["status"]!.ToString().ShouldBe("success");
        result["occurrencesReplaced"]!.GetValue<int>().ShouldBe(3);
        File.ReadAllText(filePath).ShouldBe("FOO bar FOO baz FOO");
    }

    [Fact]
    public void Run_NotFound_Throws()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "Missing", "X"));
        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public void Run_CaseInsensitiveMatch_ThrowsWithSuggestion()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun(filePath, "hello world", "X"));
        ex.Message.ShouldContain("Did you mean");
    }

    [Fact]
    public void Run_MultilineOldString_ReplacesAcrossLines()
    {
        var filePath = CreateTestFile("test.txt", "Line 1\nLine 2\nLine 3\nLine 4");

        var result = _tool.TestRun(filePath, "Line 2\nLine 3", "Replacement");

        result["status"]!.ToString().ShouldBe("success");
        File.ReadAllText(filePath).ShouldBe("Line 1\nReplacement\nLine 4");
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
    public void Run_ReturnsAffectedLines()
    {
        var filePath = CreateTestFile("test.txt", "Line 1\nLine 2\nTarget\nLine 4");

        var result = _tool.TestRun(filePath, "Target", "Replaced");

        result["affectedLines"]!["start"]!.GetValue<int>().ShouldBe(3);
        result["affectedLines"]!["end"]!.GetValue<int>().ShouldBe(3);
    }

    [Fact]
    public void Run_AtomicWrite_NoTmpFileRemains()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        _tool.TestRun(filePath, "World", "Universe");

        File.Exists(filePath + ".tmp").ShouldBeFalse();
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

    [Fact]
    public void Run_FileNotFound_Throws()
    {
        Should.Throw<FileNotFoundException>(() =>
            _tool.TestRun(Path.Combine(_testDir, "nonexistent.txt"), "old", "new"));
    }

    private string CreateTestFile(string name, string content)
    {
        var path = Path.Combine(_testDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private class TestableTextEditTool(string vaultPath, string[] allowedExtensions)
        : TextEditTool(vaultPath, allowedExtensions)
    {
        public JsonNode TestRun(string filePath, string oldString, string newString, bool replaceAll = false)
        {
            return Run(filePath, oldString, newString, replaceAll);
        }
    }
}
