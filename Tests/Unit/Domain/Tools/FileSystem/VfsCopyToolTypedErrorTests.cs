using Domain.Contracts;
using Domain.Tools;
using Domain.Tools.Config;
using Domain.Tools.Files;
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

    // A denied source never reaches the stream: the transfer probes it first to decide file versus
    // directory, and the jail refuses there. The code and the retryability are the backend's either
    // way — what must not happen is the refusal arriving as a retryable internal error.
    [Fact]
    public async Task CopyAsync_CrossFsSourceDenied_KeepsTheBackendsCodeAndRetryability()
    {
        var result = await TransferToolDriver.CopyAsync(
            new FileSystemResolution(Root(_source), "/etc/passwd"),
            new FileSystemResolution(Root(_destination), "passwd"),
            "/media/etc/passwd", "/sandbox/passwd",
            overwrite: false, createDirectories: true, ct: CancellationToken.None);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.InvalidArgument);
        result["retryable"]!.GetValue<bool>().ShouldBeFalse();
        result["message"]!.GetValue<string>().ShouldContain("Access denied");
    }

    // The source seam that does reach the stream: fs_info reports a missing path as exists=false
    // without an error, so the transfer streams and the read refuses. Its not-found is permanent,
    // and the catch-all would have relabelled it retryable.
    [Fact]
    public async Task CopyAsync_CrossFsSourceMissing_KeepsTheBackendsCodeAndRetryability()
    {
        var result = await TransferToolDriver.CopyAsync(
            new FileSystemResolution(Root(_source), "gone.bin"),
            new FileSystemResolution(Root(_destination), "gone.bin"),
            "/media/gone.bin", "/sandbox/gone.bin",
            overwrite: false, createDirectories: true, ct: CancellationToken.None);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.NotFound);
        result["retryable"]!.GetValue<bool>().ShouldBeFalse();
    }

    // The same seam on the writing side: a destination the jail refuses is permanent too.
    [Fact]
    public async Task CopyAsync_CrossFsDestinationDenied_KeepsTheBackendsCodeAndRetryability()
    {
        File.WriteAllText(Path.Combine(_source, "note.bin"), "payload");

        var result = await TransferToolDriver.CopyAsync(
            new FileSystemResolution(Root(_source), "note.bin"),
            new FileSystemResolution(Root(_destination), "/etc/note.bin"),
            "/media/note.bin", "/sandbox/etc/note.bin",
            overwrite: false, createDirectories: true, ct: CancellationToken.None);

        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.InvalidArgument);
        result["retryable"]!.GetValue<bool>().ShouldBeFalse();
    }

    private static DiskFileSystem Root(string path) =>
        new("disk", "A disk root.", new LocalFileSystemClient(), new LibraryPathConfig(path));
}