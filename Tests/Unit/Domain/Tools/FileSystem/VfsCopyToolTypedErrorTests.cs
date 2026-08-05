using Domain.Contracts;
using Domain.Tools;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Domain.Tools.FileSystem;
using Infrastructure.Clients;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

// A cross-mount transfer streams, and a stream has no envelope to carry a refusal in. The backend's
// typed error used to collapse into an IOException, which the tool's catch-all relabelled
// internal_error / retryable — so the agent was told to try again at a path that will always be
// denied. The code and the retryability the backend decided must survive the chunk path.
public class VfsCopyToolTypedErrorTests : IDisposable
{
    private readonly string _source = Path.Combine(Path.GetTempPath(), $"copy-src-{Guid.NewGuid():N}");
    private readonly string _destination = Path.Combine(Path.GetTempPath(), $"copy-dst-{Guid.NewGuid():N}");

    public VfsCopyToolTypedErrorTests()
    {
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_destination);
    }

    public void Dispose()
    {
        Directory.Delete(_source, true);
        Directory.Delete(_destination, true);
    }

    [Fact]
    public async Task TransferFileAsync_CrossFsSourceDenied_KeepsTheBackendsCodeAndRetryability()
    {
        var result = await VfsCopyTool.TransferFileAsync(
            new FileSystemResolution(Root(_source), "/etc/passwd"),
            new FileSystemResolution(Root(_destination), "passwd"),
            "/media/etc/passwd", "/sandbox/passwd",
            overwrite: false, createDirectories: true, deleteSource: false, CancellationToken.None);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.InvalidArgument);
        result["retryable"]!.GetValue<bool>().ShouldBeFalse();
        result["message"]!.GetValue<string>().ShouldContain("Access denied");
    }

    // The same seam on the writing side: a destination the jail refuses is permanent too.
    [Fact]
    public async Task TransferFileAsync_CrossFsDestinationDenied_KeepsTheBackendsCodeAndRetryability()
    {
        File.WriteAllText(Path.Combine(_source, "note.bin"), "payload");

        var result = await VfsCopyTool.TransferFileAsync(
            new FileSystemResolution(Root(_source), "note.bin"),
            new FileSystemResolution(Root(_destination), "/etc/note.bin"),
            "/media/note.bin", "/sandbox/etc/note.bin",
            overwrite: false, createDirectories: true, deleteSource: false, CancellationToken.None);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.InvalidArgument);
        result["retryable"]!.GetValue<bool>().ShouldBeFalse();
    }

    private static DiskFileSystem Root(string path) =>
        new("disk", "A disk root.", new LocalFileSystemClient(), new LibraryPathConfig(path));
}