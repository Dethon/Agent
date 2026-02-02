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
    public void ComputeFileHash_SameContent_ReturnsSameHash()
    {
        var lines1 = new[] { "Line 1", "Line 2", "Line 3" };
        var lines2 = new[] { "Line 1", "Line 2", "Line 3" };

        var hash1 = TestableTextTool.TestComputeFileHash(lines1);
        var hash2 = TestableTextTool.TestComputeFileHash(lines2);

        hash1.ShouldBe(hash2);
    }

    [Fact]
    public void ComputeFileHash_DifferentContent_ReturnsDifferentHash()
    {
        var lines1 = new[] { "Line 1", "Line 2", "Line 3" };
        var lines2 = new[] { "Line 1", "Line 2", "Line 4" };

        var hash1 = TestableTextTool.TestComputeFileHash(lines1);
        var hash2 = TestableTextTool.TestComputeFileHash(lines2);

        hash1.ShouldNotBe(hash2);
    }

    [Fact]
    public void ValidateExpectedHash_Matching_DoesNotThrow()
    {
        var lines = new[] { "Line 1", "Line 2", "Line 3" };
        var hash = TestableTextTool.TestComputeFileHash(lines);

        Should.NotThrow(() => TestableTextTool.TestValidateExpectedHash(lines, hash));
    }

    [Fact]
    public void ValidateExpectedHash_Mismatching_ThrowsWithCurrentHash()
    {
        var lines = new[] { "Line 1", "Line 2", "Line 3" };
        var wrongHash = "0000000000000000";

        var ex = Should.Throw<InvalidOperationException>(() =>
            TestableTextTool.TestValidateExpectedHash(lines, wrongHash));

        ex.Message.ShouldContain("File hash mismatch");
        ex.Message.ShouldContain(TestableTextTool.TestComputeFileHash(lines));
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

        public static string TestComputeFileHash(string[] lines)
        {
            return ComputeFileHash(lines);
        }

        public static void TestValidateExpectedHash(string[] lines, string? expectedHash)
        {
            ValidateExpectedHash(lines, expectedHash);
        }
    }
}
