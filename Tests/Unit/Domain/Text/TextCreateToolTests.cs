using System.Text.Json.Nodes;
using Domain.Tools.Text;
using Shouldly;

namespace Tests.Unit.Domain.Text;

public class TextCreateToolTests : IDisposable
{
    private readonly string _testDir;
    private readonly TestableTextCreateTool _tool;

    public TextCreateToolTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"text-create-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
        _tool = new TestableTextCreateTool(_testDir, [".md", ".txt", ".json"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    [Fact]
    public void Run_CreatesNewFile()
    {
        var result = _tool.TestRun("new-note.md", "# My Note\n\nContent here");

        result["status"]!.ToString().ShouldBe("created");
        result["filePath"]!.ToString().ShouldBe("new-note.md");

        var content = File.ReadAllText(Path.Combine(_testDir, "new-note.md"));
        content.ShouldBe("# My Note\n\nContent here");
    }

    [Fact]
    public void Run_CreatesParentDirectories()
    {
        var result = _tool.TestRun("deep/nested/path/note.md", "Content");

        result["status"]!.ToString().ShouldBe("created");
        File.Exists(Path.Combine(_testDir, "deep/nested/path/note.md")).ShouldBeTrue();
    }

    [Fact]
    public void Run_WithCreateDirectoriesFalse_FailsIfParentMissing()
    {
        Should.Throw<DirectoryNotFoundException>(() =>
            _tool.TestRun("nonexistent/note.md", "Content", createDirectories: false));
    }

    [Fact]
    public void Run_FileAlreadyExists_ThrowsException()
    {
        File.WriteAllText(Path.Combine(_testDir, "existing.md"), "Old content");

        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun("existing.md", "New content"));
        ex.Message.ShouldContain("already exists");
        ex.Message.ShouldContain("TextEdit");
    }

    [Fact]
    public void Run_DisallowedExtension_ThrowsException()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun("script.ps1", "Get-Process"));
        ex.Message.ShouldContain("not allowed");
    }

    [Fact]
    public void Run_PathOutsideVault_ThrowsException()
    {
        Should.Throw<UnauthorizedAccessException>(() =>
            _tool.TestRun("../outside.md", "Content"));
    }

    [Fact]
    public void Run_ReturnsFileMetadata()
    {
        var content = "Line 1\nLine 2\nLine 3";
        var result = _tool.TestRun("meta.md", content);

        result["lines"]!.GetValue<int>().ShouldBe(3);
        result["size"]!.ToString().ShouldNotBeEmpty();
    }

    [Fact]
    public void Run_RelativePath_ResolvesCorrectly()
    {
        var result = _tool.TestRun("notes/2024/january.md", "January notes");

        result["filePath"]!.ToString().ShouldBe("notes/2024/january.md");
        File.Exists(Path.Combine(_testDir, "notes", "2024", "january.md")).ShouldBeTrue();
    }

    [Fact]
    public void Run_LeadingSlash_ResolvesCorrectly()
    {
        var result = _tool.TestRun("docs/readme.md", "Documentation");

        result["filePath"]!.ToString().ShouldBe("docs/readme.md");
        File.Exists(Path.Combine(_testDir, "docs", "readme.md")).ShouldBeTrue();
    }

    [Fact]
    public void Run_WithOverwriteTrue_OverwritesExistingFile()
    {
        File.WriteAllText(Path.Combine(_testDir, "existing.md"), "Old content");

        var result = _tool.TestRun("existing.md", "New content", overwrite: true);

        result["status"]!.ToString().ShouldBe("created");
        File.ReadAllText(Path.Combine(_testDir, "existing.md")).ShouldBe("New content");
    }

    [Fact]
    public void Run_WithOverwriteFalse_FileExists_ThrowsException()
    {
        File.WriteAllText(Path.Combine(_testDir, "existing.md"), "Old content");

        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestRun("existing.md", "New content", overwrite: false));
        ex.Message.ShouldContain("already exists");
    }

    private class TestableTextCreateTool(string vaultPath, string[] allowedExtensions)
        : TextCreateTool(vaultPath, allowedExtensions)
    {
        public JsonNode TestRun(
            string filePath,
            string content,
            bool overwrite = false,
            bool createDirectories = true)
        {
            return Run(filePath, content, overwrite, createDirectories);
        }
    }
}
