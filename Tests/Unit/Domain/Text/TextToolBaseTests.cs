using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.Text;
using Shouldly;

namespace Tests.Unit.Domain.Text;

public class TextToolBaseTests : IDisposable
{
    private readonly string _testDir;
    private readonly TestableTextTool _tool;

    public TextToolBaseTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"text-tool-base-tests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
        _tool = new TestableTextTool(_testDir, [".md", ".txt"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    [Fact]
    public void ResolveExistingFile_ValidFile_ReturnsFullPath()
    {
        var filePath = CreateTestFile("test.md", "content");

        _tool.TestResolveExistingFile("test.md").TryGetValue(out var resolved, out _).ShouldBeTrue();

        resolved.ShouldBe(filePath);
    }

    [Fact]
    public void ResolveExistingFile_PathOutsideVault_ReturnsInvalidArgument()
    {
        ErrorFor("/etc/passwd").ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
    }

    // A directory whose name merely extends the vault's is a different directory, and a prefix
    // match without a separator used to let it through.
    [Fact]
    public void ResolveExistingFile_SiblingDirectoryWithVaultPrefix_ReturnsInvalidArgument()
    {
        var sibling = _testDir + "-evil";
        Directory.CreateDirectory(sibling);
        try
        {
            File.WriteAllText(Path.Combine(sibling, "secret.md"), "leak");

            ErrorFor(Path.Combine(sibling, "secret.md")).ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
        }
        finally
        {
            Directory.Delete(sibling, true);
        }
    }

    [Fact]
    public void ResolveExistingFile_FileNotFound_ReturnsNotFound()
    {
        ErrorFor("nonexistent.md").ErrorCode.ShouldBe(ToolError.Codes.NotFound);
    }

    [Fact]
    public void ResolveExistingFile_DisallowedExtension_ReturnsInvalidArgument()
    {
        File.WriteAllText(Path.Combine(_testDir, "test.exe"), "content");

        var error = ErrorFor("test.exe");

        error.ErrorCode.ShouldBe(ToolError.Codes.InvalidArgument);
        error.Message.ShouldContain("not allowed");
    }

    private ToolErrorResult ErrorFor(string filePath)
    {
        _tool.TestResolveExistingFile(filePath).TryGetValue(out _, out var error).ShouldBeFalse();
        return error!;
    }

    private string CreateTestFile(string name, string content)
    {
        var path = Path.Combine(_testDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private class TestableTextTool(string vaultPath, string[] allowedExtensions)
        : TextToolBase(vaultPath, allowedExtensions)
    {
        public FsResult<string> TestResolveExistingFile(string filePath) => ResolveExistingFile(filePath);
    }
}