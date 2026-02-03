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
    public void ValidateAndResolvePath_ValidFile_ReturnsFullPath()
    {
        var filePath = CreateTestFile("test.md", "content");

        var result = _tool.TestValidateAndResolvePath("test.md");

        result.ShouldBe(filePath);
    }

    [Fact]
    public void ValidateAndResolvePath_PathOutsideVault_ThrowsException()
    {
        Should.Throw<UnauthorizedAccessException>(() =>
            _tool.TestValidateAndResolvePath("/etc/passwd"));
    }

    [Fact]
    public void ValidateAndResolvePath_FileNotFound_ThrowsException()
    {
        Should.Throw<FileNotFoundException>(() =>
            _tool.TestValidateAndResolvePath("nonexistent.md"));
    }

    [Fact]
    public void ValidateAndResolvePath_DisallowedExtension_ThrowsException()
    {
        var filePath = Path.Combine(_testDir, "test.exe");
        File.WriteAllText(filePath, "content");

        var ex = Should.Throw<InvalidOperationException>(() =>
            _tool.TestValidateAndResolvePath("test.exe"));

        ex.Message.ShouldContain("not allowed");
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
        public string TestValidateAndResolvePath(string filePath)
        {
            return ValidateAndResolvePath(filePath);
        }
    }
}