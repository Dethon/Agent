using System.Text.Json.Nodes;
using Domain.Tools.Files;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Files;

public class CopyToolTests : IDisposable
{
    private readonly string _root;
    private readonly TestableCopyTool _tool;

    public CopyToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"copytool-{Guid.NewGuid()}");
        Directory.CreateDirectory(_root);
        _tool = new TestableCopyTool(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public void Run_FileToNewFile_CopiesContent()
    {
        File.WriteAllText(Path.Combine(_root, "src.txt"), "hello");

        var result = _tool.TestRun("src.txt", "dst.txt", overwrite: false, createDirectories: true);

        File.ReadAllText(Path.Combine(_root, "dst.txt")).ShouldBe("hello");
        result["status"]!.GetValue<string>().ShouldBe("copied");
        result["bytes"]!.GetValue<long>().ShouldBe(5);
    }

    [Fact]
    public void Run_DestinationExistsAndOverwriteFalse_Throws()
    {
        File.WriteAllText(Path.Combine(_root, "src.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "dst.txt"), "y");

        Should.Throw<IOException>(() =>
            _tool.TestRun("src.txt", "dst.txt", overwrite: false, createDirectories: true));
    }

    [Fact]
    public void Run_DestinationExistsAndOverwriteTrue_Replaces()
    {
        File.WriteAllText(Path.Combine(_root, "src.txt"), "new");
        File.WriteAllText(Path.Combine(_root, "dst.txt"), "old");

        _tool.TestRun("src.txt", "dst.txt", overwrite: true, createDirectories: true);

        File.ReadAllText(Path.Combine(_root, "dst.txt")).ShouldBe("new");
    }

    [Fact]
    public void Run_DirectorySource_CopiesRecursively()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "sub"));
        File.WriteAllText(Path.Combine(_root, "src", "a.txt"), "A");
        File.WriteAllText(Path.Combine(_root, "src", "sub", "b.txt"), "B");

        _tool.TestRun("src", "dst", overwrite: false, createDirectories: true);

        File.ReadAllText(Path.Combine(_root, "dst", "a.txt")).ShouldBe("A");
        File.ReadAllText(Path.Combine(_root, "dst", "sub", "b.txt")).ShouldBe("B");
    }

    [Fact]
    public void Run_PathOutsideRoot_Throws()
    {
        Should.Throw<UnauthorizedAccessException>(() =>
            _tool.TestRun("../escape.txt", "dst.txt", overwrite: false, createDirectories: true));
    }

    [Fact]
    public void Run_PathToSiblingDirectoryWithRootPrefix_Throws()
    {
        var sibling = _root + "-evil";
        Directory.CreateDirectory(sibling);
        try
        {
            File.WriteAllText(Path.Combine(sibling, "secret.txt"), "leak");
            var rootName = Path.GetFileName(_root);
            var malicious = $"../{rootName}-evil/secret.txt";

            Should.Throw<UnauthorizedAccessException>(() =>
                _tool.TestRun(malicious, "dst.txt", overwrite: false, createDirectories: true));
        }
        finally
        {
            if (Directory.Exists(sibling))
            {
                Directory.Delete(sibling, true);
            }

        }
    }

    private class TestableCopyTool(string root) : CopyTool(root)
    {
        public JsonNode TestRun(string source, string destination, bool overwrite, bool createDirectories)
            => Run(source, destination, overwrite, createDirectories);
    }
}
