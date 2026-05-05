using System.Text.Json.Nodes;
using Domain.DTOs;
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
    public void Run_SingleEdit_ReplacesText()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var result = _tool.TestRun(filePath, [new TextEdit("World", "Universe")]);

        result["status"]!.ToString().ShouldBe("success");
        result["totalOccurrencesReplaced"]!.GetValue<int>().ShouldBe(1);
        result["edits"]!.AsArray().Count.ShouldBe(1);
        result["edits"]![0]!["occurrencesReplaced"]!.GetValue<int>().ShouldBe(1);
        File.ReadAllText(filePath).ShouldBe("Hello Universe");
    }

    [Fact]
    public void Run_MultipleOccurrences_ReplaceAllFalse_Throws()
    {
        var filePath = CreateTestFile("test.txt", "foo bar foo baz foo");

        var ex = Should.Throw<ArgumentException>(() =>
            _tool.TestRun(filePath, [new TextEdit("foo", "FOO")]));
        ex.Message.ShouldContain("3 occurrences");
        ex.Message.ShouldContain("disambiguate");
        File.ReadAllText(filePath).ShouldBe("foo bar foo baz foo");
    }

    [Fact]
    public void Run_MultipleOccurrences_ReplaceAllTrue_ReplacesAll()
    {
        var filePath = CreateTestFile("test.txt", "foo bar foo baz foo");

        var result = _tool.TestRun(filePath, [new TextEdit("foo", "FOO", ReplaceAll: true)]);

        result["status"]!.ToString().ShouldBe("success");
        result["totalOccurrencesReplaced"]!.GetValue<int>().ShouldBe(3);
        File.ReadAllText(filePath).ShouldBe("FOO bar FOO baz FOO");
    }

    [Fact]
    public void Run_NotFound_Throws()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var ex = Should.Throw<ArgumentException>(() =>
            _tool.TestRun(filePath, [new TextEdit("Missing", "X")]));
        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public void Run_CaseInsensitiveMatch_ThrowsWithSuggestion()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        var ex = Should.Throw<ArgumentException>(() =>
            _tool.TestRun(filePath, [new TextEdit("hello world", "X")]));
        ex.Message.ShouldContain("Did you mean");
    }

    [Fact]
    public void Run_MultilineOldString_ReplacesAcrossLines()
    {
        var filePath = CreateTestFile("test.txt", "Line 1\nLine 2\nLine 3\nLine 4");

        var result = _tool.TestRun(filePath, [new TextEdit("Line 2\nLine 3", "Replacement")]);

        result["status"]!.ToString().ShouldBe("success");
        File.ReadAllText(filePath).ShouldBe("Line 1\nReplacement\nLine 4");
    }

    [Fact]
    public void Run_ReturnsAffectedLinesPerEdit()
    {
        var filePath = CreateTestFile("test.txt", "Line 1\nLine 2\nTarget\nLine 4");

        var result = _tool.TestRun(filePath, [new TextEdit("Target", "Replaced")]);

        result["edits"]![0]!["affectedLines"]!["start"]!.GetValue<int>().ShouldBe(3);
        result["edits"]![0]!["affectedLines"]!["end"]!.GetValue<int>().ShouldBe(3);
    }

    [Fact]
    public void Run_AtomicWrite_NoTmpFileRemains()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");

        _tool.TestRun(filePath, [new TextEdit("World", "Universe")]);

        File.Exists(filePath + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public void Run_MultipleEdits_AppliedInOrder()
    {
        var filePath = CreateTestFile("test.txt", "alpha beta gamma");

        var result = _tool.TestRun(filePath,
        [
            new TextEdit("alpha", "ALPHA"),
            new TextEdit("beta", "BETA"),
            new TextEdit("gamma", "GAMMA")
        ]);

        result["status"]!.ToString().ShouldBe("success");
        result["totalOccurrencesReplaced"]!.GetValue<int>().ShouldBe(3);
        result["edits"]!.AsArray().Count.ShouldBe(3);
        File.ReadAllText(filePath).ShouldBe("ALPHA BETA GAMMA");
    }

    [Fact]
    public void Run_LaterEdit_CanMatchTextProducedByEarlierEdit()
    {
        var filePath = CreateTestFile("test.txt", "one");

        var result = _tool.TestRun(filePath,
        [
            new TextEdit("one", "two"),
            new TextEdit("two", "three")
        ]);

        result["totalOccurrencesReplaced"]!.GetValue<int>().ShouldBe(2);
        File.ReadAllText(filePath).ShouldBe("three");
    }

    [Fact]
    public void Run_MidSequenceFailure_FileUnchanged()
    {
        var filePath = CreateTestFile("test.txt", "alpha beta");
        var originalContent = File.ReadAllText(filePath);

        Should.Throw<ArgumentException>(() =>
            _tool.TestRun(filePath,
            [
                new TextEdit("alpha", "ALPHA"),
                new TextEdit("does-not-exist", "X"),
                new TextEdit("beta", "BETA")
            ]));

        File.ReadAllText(filePath).ShouldBe(originalContent);
        File.Exists(filePath + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public void Run_EmptyEditsArray_Throws()
    {
        var filePath = CreateTestFile("test.txt", "content");

        var ex = Should.Throw<ArgumentException>(() =>
            _tool.TestRun(filePath, []));
        ex.Message.ShouldContain("edits");
    }

    [Fact]
    public void Run_TotalOccurrencesIsSumOfPerEditCounts()
    {
        var filePath = CreateTestFile("test.txt", "a a a b b");

        var result = _tool.TestRun(filePath,
        [
            new TextEdit("a", "A", ReplaceAll: true),
            new TextEdit("b", "B", ReplaceAll: true)
        ]);

        result["totalOccurrencesReplaced"]!.GetValue<int>().ShouldBe(5);
        result["edits"]![0]!["occurrencesReplaced"]!.GetValue<int>().ShouldBe(3);
        result["edits"]![1]!["occurrencesReplaced"]!.GetValue<int>().ShouldBe(2);
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
        public JsonNode TestRun(string filePath, IReadOnlyList<TextEdit> edits)
        {
            return Run(filePath, edits);
        }
    }
}
