using System.Text.Json.Nodes;
using Domain.Tools.Text;
using Shouldly;

namespace Tests.Unit.Domain.Text;

public class TextReadToolTests : IDisposable
{
    private readonly string _testDir;
    private readonly TestableTextReadTool _tool;

    public TextReadToolTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"text-read-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
        _tool = new TestableTextReadTool(_testDir, [".md", ".txt"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    [Fact]
    public void Run_LinesTarget_ReturnsSpecifiedLines()
    {
        var content = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"Line {i}"));
        var filePath = CreateTestFile("test.md", content);

        var target = new JsonObject { ["lines"] = new JsonObject { ["start"] = 3, ["end"] = 5 } };
        var result = _tool.TestRun(filePath, target);

        result["content"]!.ToString().ShouldBe("Line 3\nLine 4\nLine 5");
        result["range"]!["startLine"]!.GetValue<int>().ShouldBe(3);
        result["range"]!["endLine"]!.GetValue<int>().ShouldBe(5);
    }

    [Fact]
    public void Run_HeadingTarget_ReturnsHeadingSection()
    {
        var content = """
                      # Introduction
                      Intro content
                      ## Setup
                      Setup content line 1
                      Setup content line 2
                      ## Configuration
                      Config content
                      """;
        var filePath = CreateTestFile("doc.md", content);

        var target = new JsonObject
        {
            ["heading"] = new JsonObject { ["text"] = "Setup", ["includeChildren"] = false }
        };
        var result = _tool.TestRun(filePath, target);

        result["content"]!.ToString().ShouldContain("Setup content");
        result["content"]!.ToString().ShouldNotContain("Config content");
    }

    [Fact]
    public void Run_CodeBlockTarget_ReturnsCodeBlock()
    {
        var content = """
                      # Examples
                      ```csharp
                      var x = 1;
                      var y = 2;
                      ```
                      Some text
                      ```python
                      print("hello")
                      ```
                      """;
        var filePath = CreateTestFile("code.md", content);

        var target = new JsonObject { ["codeBlock"] = new JsonObject { ["index"] = 1 } };
        var result = _tool.TestRun(filePath, target);

        result["content"]!.ToString().ShouldContain("python");
        result["content"]!.ToString().ShouldContain("print");
    }

    [Fact]
    public void Run_SectionTarget_ReturnsIniSection()
    {
        var content = """
                      [database]
                      host=localhost
                      port=5432

                      [cache]
                      enabled=true
                      """;
        var filePath = CreateTestFile("config.txt", content);

        var target = new JsonObject { ["section"] = "[database]" };
        var result = _tool.TestRun(filePath, target);

        result["content"]!.ToString().ShouldContain("host=localhost");
        result["content"]!.ToString().ShouldNotContain("enabled=true");
    }

    [Fact]
    public void Run_SearchTarget_ReturnsContextAroundMatch()
    {
        var content = """
                      Line 1
                      Line 2
                      Target text here
                      Line 4
                      Line 5
                      """;
        var filePath = CreateTestFile("search.md", content);

        var target = new JsonObject
        {
            ["search"] = new JsonObject { ["query"] = "Target", ["contextLines"] = 2 }
        };
        var result = _tool.TestRun(filePath, target);

        result["content"]!.ToString().ShouldContain("Line 1");
        result["content"]!.ToString().ShouldContain("Target text");
        result["content"]!.ToString().ShouldContain("Line 5");
    }

    [Fact]
    public void Run_LargeSection_IsTruncated()
    {
        var content = string.Join("\n", Enumerable.Range(1, 500).Select(i => $"Line {i}"));
        var filePath = CreateTestFile("large.md", content);

        var target = new JsonObject { ["lines"] = new JsonObject { ["start"] = 1, ["end"] = 500 } };
        var result = _tool.TestRun(filePath, target);

        result["truncated"]!.GetValue<bool>().ShouldBeTrue();
        result["suggestion"]!.ToString().ShouldNotBeEmpty();
    }

    [Fact]
    public void Run_HeadingNotFound_ThrowsWithSuggestions()
    {
        var content = """
                      # Introduction
                      ## Installation
                      ## Configuration
                      """;
        var filePath = CreateTestFile("doc.md", content);

        var target = new JsonObject
        {
            ["heading"] = new JsonObject { ["text"] = "Instalation" } // Typo
        };

        var ex = Should.Throw<InvalidOperationException>(() => _tool.TestRun(filePath, target));
        ex.Message.ShouldContain("not found");
    }

    private string CreateTestFile(string name, string content)
    {
        var path = Path.Combine(_testDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private class TestableTextReadTool(string vaultPath, string[] allowedExtensions)
        : TextReadTool(vaultPath, allowedExtensions)
    {
        public JsonNode TestRun(string filePath, JsonObject target)
        {
            return Run(filePath, target);
        }
    }
}