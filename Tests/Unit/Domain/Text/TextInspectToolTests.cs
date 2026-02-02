using System.Text.Json.Nodes;
using Domain.Tools.Text;
using Shouldly;

namespace Tests.Unit.Domain.Text;

public class TextInspectToolTests : IDisposable
{
    private readonly string _testDir;
    private readonly TestableTextInspectTool _tool;

    public TextInspectToolTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"text-inspect-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
        _tool = new TestableTextInspectTool(_testDir, [".md", ".txt", ".json"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    [Fact]
    public void Run_StructureMode_ReturnsMarkdownStructure()
    {
        var content = """
                      ---
                      title: Test
                      ---
                      # Heading 1
                      Some content
                      ## Heading 2
                      ```csharp
                      var x = 1;
                      ```
                      """;
        var filePath = CreateTestFile("test.md", content);

        var result = _tool.TestRun(filePath);

        result["format"]!.ToString().ShouldBe("markdown");
        result["totalLines"]!.GetValue<int>().ShouldBe(9);

        var structure = result["structure"]!;
        structure["frontmatter"]!["keys"]!.AsArray().Count.ShouldBe(1);
        structure["headings"]!.AsArray().Count.ShouldBe(2);
        structure["codeBlocks"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public void Run_StructureMode_PlainText_ReturnsSections()
    {
        var content = """
                      [database]
                      host=localhost

                      [cache]
                      enabled=true
                      """;
        var filePath = CreateTestFile("config.txt", content);

        var result = _tool.TestRun(filePath);

        result["format"]!.ToString().ShouldBe("text");
        var structure = result["structure"]!;
        structure["sections"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public void Run_DisallowedExtension_ThrowsException()
    {
        var filePath = CreateTestFile("script.ps1", "Get-Process");

        Should.Throw<InvalidOperationException>(() => _tool.TestRun(filePath))
            .Message.ShouldContain("not allowed");
    }

    [Fact]
    public void Run_PathOutsideVault_ThrowsException()
    {
        Should.Throw<UnauthorizedAccessException>(() => _tool.TestRun("/etc/passwd"));
    }

    [Fact]
    public void Run_StructureMode_ReturnsFileHash()
    {
        var content = """
                      # Test Document
                      Some content here.
                      """;
        var filePath = CreateTestFile("test.md", content);

        var result = _tool.TestRun(filePath);

        result["fileHash"].ShouldNotBeNull();
        var hash = result["fileHash"]!.ToString();
        hash.Length.ShouldBe(16);
        hash.ShouldMatch("^[a-f0-9]{16}$");
    }

    [Fact]
    public void Run_StructureMode_FileHash_ChangesWhenContentChanges()
    {
        var content1 = "# Original Content";
        var filePath = CreateTestFile("mutable.md", content1);

        var result1 = _tool.TestRun(filePath);
        var hash1 = result1["fileHash"]!.ToString();

        var content2 = "# Modified Content";
        File.WriteAllText(filePath, content2);

        var result2 = _tool.TestRun(filePath);
        var hash2 = result2["fileHash"]!.ToString();

        hash1.ShouldNotBe(hash2);
    }

    private string CreateTestFile(string name, string content)
    {
        var path = Path.Combine(_testDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private class TestableTextInspectTool(string vaultPath, string[] allowedExtensions)
        : TextInspectTool(vaultPath, allowedExtensions)
    {
        public JsonNode TestRun(string filePath)
        {
            return Run(filePath);
        }
    }
}
