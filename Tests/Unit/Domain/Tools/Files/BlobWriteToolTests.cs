using System.Text.Json.Nodes;
using Domain.Tools.Files;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Files;

public class BlobWriteToolTests : IDisposable
{
    private readonly string _root;
    private readonly TestableBlobWriteTool _tool;

    public BlobWriteToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"blobwrite-{Guid.NewGuid()}");
        Directory.CreateDirectory(_root);
        _tool = new TestableBlobWriteTool(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void Run_FirstChunkCreatesFile()
    {
        var data = new byte[] { 1, 2, 3, 4 };
        var b64 = Convert.ToBase64String(data);

        var result = _tool.TestRun("out.bin", b64, offset: 0, overwrite: false, createDirectories: true);

        File.ReadAllBytes(Path.Combine(_root, "out.bin")).ShouldBe(data);
        result["bytesWritten"]!.GetValue<int>().ShouldBe(4);
        result["totalBytes"]!.GetValue<long>().ShouldBe(4);
    }

    [Fact]
    public void Run_AppendsAtOffset()
    {
        File.WriteAllBytes(Path.Combine(_root, "out.bin"), new byte[] { 1, 2 });
        var b64 = Convert.ToBase64String(new byte[] { 3, 4 });

        var result = _tool.TestRun("out.bin", b64, offset: 2, overwrite: true, createDirectories: true);

        File.ReadAllBytes(Path.Combine(_root, "out.bin")).ShouldBe(new byte[] { 1, 2, 3, 4 });
        result["totalBytes"]!.GetValue<long>().ShouldBe(4);
    }

    [Fact]
    public void Run_OffsetZeroOverwriteFalseAndExists_Throws()
    {
        File.WriteAllBytes(Path.Combine(_root, "out.bin"), new byte[] { 1 });
        var b64 = Convert.ToBase64String(new byte[] { 9 });

        Should.Throw<IOException>(() =>
            _tool.TestRun("out.bin", b64, offset: 0, overwrite: false, createDirectories: true));
    }

    [Fact]
    public void Run_OffsetZeroOverwriteTrueAndExists_Replaces()
    {
        File.WriteAllBytes(Path.Combine(_root, "out.bin"), new byte[] { 1, 2, 3 });
        var b64 = Convert.ToBase64String(new byte[] { 9 });

        _tool.TestRun("out.bin", b64, offset: 0, overwrite: true, createDirectories: true);

        File.ReadAllBytes(Path.Combine(_root, "out.bin")).ShouldBe(new byte[] { 9 });
    }

    [Fact]
    public void Run_CreatesParentDirectories()
    {
        var b64 = Convert.ToBase64String(new byte[] { 1 });

        _tool.TestRun("nested/dir/out.bin", b64, offset: 0, overwrite: false, createDirectories: true);

        File.Exists(Path.Combine(_root, "nested", "dir", "out.bin")).ShouldBeTrue();
    }

    [Fact]
    public void Run_PathToSiblingDirectoryWithRootPrefix_Throws()
    {
        var sibling = _root + "-evil";
        Directory.CreateDirectory(sibling);
        try
        {
            var rootName = Path.GetFileName(_root);
            var malicious = $"../{rootName}-evil/out.bin";
            var b64 = Convert.ToBase64String(new byte[] { 1 });

            Should.Throw<UnauthorizedAccessException>(() =>
                _tool.TestRun(malicious, b64, offset: 0, overwrite: false, createDirectories: true));
        }
        finally
        {
            if (Directory.Exists(sibling)) Directory.Delete(sibling, true);
        }
    }

    [Fact]
    public void Run_NegativeOffset_Throws()
    {
        var b64 = Convert.ToBase64String(new byte[] { 1 });

        Should.Throw<ArgumentOutOfRangeException>(() =>
            _tool.TestRun("out.bin", b64, offset: -1, overwrite: false, createDirectories: true));
    }

    private class TestableBlobWriteTool(string root) : BlobWriteTool(root)
    {
        public JsonNode TestRun(string path, string contentBase64, long offset, bool overwrite, bool createDirectories)
            => Run(path, contentBase64, offset, overwrite, createDirectories);
    }
}
