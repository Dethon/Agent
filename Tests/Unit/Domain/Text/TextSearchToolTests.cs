using System.Text.Json.Nodes;
using Domain.Tools.Text;
using Shouldly;

namespace Tests.Unit.Domain.Text;

public class TextSearchToolTests : IDisposable
{
    private readonly string _testDir;
    private readonly TestableTextSearchTool _tool;

    public TextSearchToolTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"text-search-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
        _tool = new TestableTextSearchTool(_testDir, [".md", ".txt"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    [Fact]
    public void Run_FindsMatchesAcrossMultipleFiles()
    {
        CreateTestFile("doc1.md", "# About Kubernetes\nKubernetes is great");
        CreateTestFile("doc2.md", "# Setup\nNo match here");
        CreateTestFile("doc3.md", "# Config\nConfigure kubernetes cluster");

        var result = _tool.TestRun("kubernetes");

        result["filesWithMatches"]!.GetValue<int>().ShouldBe(2);
        result["totalMatches"]!.GetValue<int>().ShouldBe(3);
    }

    [Fact]
    public void Run_WithFilePattern_FiltersFiles()
    {
        CreateTestFile("readme.md", "Important info");
        CreateTestFile("notes.txt", "Important notes");

        var result = _tool.TestRun("Important", filePattern: "*.md");

        result["filesWithMatches"]!.GetValue<int>().ShouldBe(1);
        result["results"]!.AsArray()[0]!["file"]!.ToString().ShouldEndWith(".md");
    }

    [Fact]
    public void Run_WithSubdirectory_SearchesRecursively()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, "subdir"));
        CreateTestFile("root.md", "Target word");
        CreateTestFile("subdir/nested.md", "Another target");

        var result = _tool.TestRun("target");

        result["filesWithMatches"]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public void Run_WithRegex_MatchesPattern()
    {
        CreateTestFile("todos.md", "TODO: Fix bug\nFIXME: Later\nTODO: Add test");

        var result = _tool.TestRun("TODO:.*", regex: true);

        result["totalMatches"]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public void Run_IncludesNearestHeading()
    {
        CreateTestFile("doc.md", "# Introduction\nSome text\n## Setup\nFind this target");

        var result = _tool.TestRun("target");

        var match = result["results"]!.AsArray()[0]!["matches"]!.AsArray()[0]!;
        match["section"]!.ToString().ShouldBe("Setup");
    }

    [Fact]
    public void Run_WithContextLines_IncludesContext()
    {
        CreateTestFile("doc.md", "Line 1\nLine 2\nTarget line\nLine 4\nLine 5");

        var result = _tool.TestRun("Target", contextLines: 2);

        var match = result["results"]!.AsArray()[0]!["matches"]!.AsArray()[0]!;
        var context = match["context"]!;
        context["before"]!.AsArray().Count.ShouldBe(2);
        context["after"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public void Run_RespectsMaxResults()
    {
        var content = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"match line {i}"));
        CreateTestFile("many.md", content);

        var result = _tool.TestRun("match", maxResults: 10);

        result["totalMatches"]!.GetValue<int>().ShouldBe(10);
        result["truncated"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void Run_CaseInsensitiveByDefault()
    {
        CreateTestFile("doc.md", "KUBERNETES\nkubernetes\nKubernetes");

        var result = _tool.TestRun("kubernetes");

        result["totalMatches"]!.GetValue<int>().ShouldBe(3);
    }

    [Fact]
    public void Run_NoMatches_ReturnsEmptyResults()
    {
        CreateTestFile("doc.md", "Some content here");

        var result = _tool.TestRun("nonexistent");

        result["filesWithMatches"]!.GetValue<int>().ShouldBe(0);
        result["totalMatches"]!.GetValue<int>().ShouldBe(0);
        result["results"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public void Run_SkipsDisallowedExtensions()
    {
        CreateTestFile("doc.md", "Find this");
        CreateTestFile("script.ps1", "Find this too");

        var result = _tool.TestRun("Find");

        result["filesWithMatches"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public void Run_RelativePathsInResults()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, "docs"));
        CreateTestFile("docs/guide.md", "Target content");

        var result = _tool.TestRun("Target");

        var file = result["results"]!.AsArray()[0]!["file"]!.ToString();
        file.ShouldBe("docs/guide.md");
        file.ShouldNotContain("\\");
    }

    // --- Single-file search tests ---

    [Fact]
    public void Run_WithFilePath_SearchesSingleFile()
    {
        CreateTestFile("target.md", "Find this line\nAnd this one too");
        CreateTestFile("other.md", "Find this also");

        var filePath = Path.Combine(_testDir, "target.md");
        var result = _tool.TestRun("Find", filePath: filePath);

        result["filesSearched"]!.GetValue<int>().ShouldBe(1);
        result["filesWithMatches"]!.GetValue<int>().ShouldBe(1);
        result["totalMatches"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public void Run_WithFilePath_IgnoresDirectoryPathAndFilePattern()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, "subdir"));
        CreateTestFile("subdir/target.md", "Find this line");
        CreateTestFile("other.md", "Find this also");

        var filePath = Path.Combine(_testDir, "subdir", "target.md");
        var result = _tool.TestRun("Find", filePath: filePath, filePattern: "*.txt", directoryPath: "/nonexistent");

        result["filesSearched"]!.GetValue<int>().ShouldBe(1);
        result["totalMatches"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public void Run_WithFilePath_FileNotFound_Throws()
    {
        var filePath = Path.Combine(_testDir, "nonexistent.md");

        Should.Throw<FileNotFoundException>(() =>
            _tool.TestRun("query", filePath: filePath));
    }

    [Fact]
    public void Run_WithFilePath_OutsideVault_Throws()
    {
        Should.Throw<UnauthorizedAccessException>(() =>
            _tool.TestRun("query", filePath: "/etc/passwd"));
    }

    [Fact]
    public void Run_WithFilePath_DisallowedExtension_Throws()
    {
        CreateTestFile("script.ps1", "Find this");

        var filePath = Path.Combine(_testDir, "script.ps1");

        Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun("Find", filePath: filePath));
    }

    [Fact]
    public void Run_WithFilePath_NoMatches_ReturnsEmpty()
    {
        CreateTestFile("target.md", "No matching content here");

        var filePath = Path.Combine(_testDir, "target.md");
        var result = _tool.TestRun("nonexistent", filePath: filePath);

        result["filesWithMatches"]!.GetValue<int>().ShouldBe(0);
        result["totalMatches"]!.GetValue<int>().ShouldBe(0);
    }

    [Fact]
    public void Run_WithFilePath_WithRegex_MatchesPattern()
    {
        CreateTestFile("target.md", "TODO: Fix bug\nFIXME: Later\nTODO: Add test");

        var filePath = Path.Combine(_testDir, "target.md");
        var result = _tool.TestRun("TODO:.*", regex: true, filePath: filePath);

        result["totalMatches"]!.GetValue<int>().ShouldBe(2);
    }

    private void CreateTestFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_testDir, relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(fullPath, content);
    }

    private class TestableTextSearchTool(string vaultPath, string[] allowedExtensions)
        : TextSearchTool(vaultPath, allowedExtensions)
    {
        public JsonNode TestRun(
            string query,
            bool regex = false,
            string? filePath = null,
            string? filePattern = null,
            string directoryPath = "/",
            int maxResults = 50,
            int contextLines = 1)
        {
            return Run(query, regex, filePath, filePattern, directoryPath, maxResults, contextLines);
        }
    }
}
