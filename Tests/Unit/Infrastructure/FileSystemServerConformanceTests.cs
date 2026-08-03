using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.Files;
using Domain.Tools.HomeAssistant.Vfs;
using Domain.Tools.Printing.Vfs;
using Domain.Tools.Scheduling.Vfs;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Agents.Mcp;
using Infrastructure.Utils;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Shouldly;

namespace Tests.Unit.Infrastructure;

// The point of the whole feature: for every filesystem server, the fs_* tools it advertises, the
// operations its backend overrides, and the capabilities the mount publishes are the same set.
// A server that registered a tool its backend does not implement fails here — which is how the
// timers move lie was found: fs_move was advertised by a method that only said "unsupported".
public class FileSystemServerConformanceTests
{
    public static TheoryData<string, Type> Backends() => new()
    {
        { "timers", typeof(TimerFileSystem) },
        { "schedules", typeof(ScheduleFileSystem) },
        { "print-queue", typeof(PrinterQueueFileSystem) },
        { "ha", typeof(HaFileSystem) },
        { "media", typeof(MediaLibraryDiskFileSystem) },
        { "vault", typeof(TextDiskFileSystem) },
        { "sandbox", typeof(SandboxFileSystem) }
    };

    [Theory]
    [MemberData(nameof(Backends))]
    public void AdvertisedTools_EqualOverriddenOperations_EqualPublishedCapabilities(string name, Type backendType)
    {
        var overridden = FileSystemServerTools.SupportedToolNames(backendType);
        overridden.ShouldNotBeEmpty($"{name} advertises no filesystem tools");

        // What the server really registers, driven through the registrar exactly as its
        // ConfigModule does.
        var services = new ServiceCollection();
        typeof(FileSystemServerTools)
            .GetMethod(nameof(FileSystemServerTools.AddFileSystemTools))!
            .MakeGenericMethod(backendType)
            .Invoke(null, [services.AddMcpServer()]);
        var registered = services.Count(d => d.ServiceType == typeof(McpServerTool));

        registered.ShouldBe(overridden.Count, name);

        // What the mount publishes to the model: every advertised operation the model can call,
        // and nothing else. The two blob tools are transfer machinery, not model-facing.
        var capabilities = McpFileSystemDiscovery.DeriveCapabilities(overridden);
        capabilities.Count.ShouldBe(overridden.Count(t => !t.StartsWith("fs_blob", StringComparison.Ordinal)), name);
    }

    // The lie this feature removes: timers registered fs_move from a method whose own description
    // said the operation was unsupported, so the prompt promised the model an operation that could
    // only fail. Nothing overrides move now, so nothing can advertise it.
    [Theory]
    [InlineData(typeof(TimerFileSystem))]
    [InlineData(typeof(PrinterQueueFileSystem))]
    public void AMountThatNeverImplementedMove_DoesNotAdvertiseIt(Type backendType)
    {
        var advertised = FileSystemServerTools.SupportedToolNames(backendType);

        advertised.ShouldNotContain("fs_move");
        McpFileSystemDiscovery.DeriveCapabilities(advertised).ShouldNotContain("move");
    }

    [Fact]
    public void EachServer_AdvertisesExactlyWhatItsBackendImplements()
    {
        FileSystemServerTools.SupportedToolNames(typeof(TimerFileSystem))
            .ShouldBe(["fs_read", "fs_info", "fs_glob", "fs_search", "fs_create", "fs_edit", "fs_delete", "fs_exec"],
                ignoreOrder: true);

        FileSystemServerTools.SupportedToolNames(typeof(ScheduleFileSystem))
            .ShouldBe(["fs_read", "fs_info", "fs_glob", "fs_search", "fs_create", "fs_edit", "fs_move", "fs_delete", "fs_exec"],
                ignoreOrder: true);

        FileSystemServerTools.SupportedToolNames(typeof(HaFileSystem))
            .ShouldBe(["fs_read", "fs_info", "fs_glob", "fs_search", "fs_exec"], ignoreOrder: true);

        FileSystemServerTools.SupportedToolNames(typeof(PrinterQueueFileSystem))
            .ShouldBe([
                "fs_read", "fs_info", "fs_glob", "fs_search", "fs_create", "fs_edit",
                "fs_delete", "fs_copy", "fs_blob_read", "fs_blob_write"
            ], ignoreOrder: true);

        // The media library reads only the overlay's status file and writes no text, so it keeps
        // the plain disk surface plus read.
        FileSystemServerTools.SupportedToolNames(typeof(MediaLibraryDiskFileSystem))
            .ShouldBe([
                "fs_read", "fs_info", "fs_glob", "fs_move", "fs_delete", "fs_copy",
                "fs_blob_read", "fs_blob_write"
            ], ignoreOrder: true);

        FileSystemServerTools.SupportedToolNames(typeof(TextDiskFileSystem))
            .ShouldBe([
                "fs_read", "fs_info", "fs_glob", "fs_search", "fs_create", "fs_edit", "fs_move",
                "fs_delete", "fs_copy", "fs_blob_read", "fs_blob_write"
            ], ignoreOrder: true);

        // Exec is the one thing the sandbox has and the vault does not.
        FileSystemServerTools.SupportedToolNames(typeof(SandboxFileSystem))
            .ShouldBe(
                [.. FileSystemServerTools.SupportedToolNames(typeof(TextDiskFileSystem)), "fs_exec"],
                ignoreOrder: true);
    }

    // Capability is per operation, not per path. A backend that implements an operation and still
    // refuses particular paths keeps advertising it — the list tells the model which operations
    // exist on a mount, not which will succeed on a given file. Nobody should refine this into a
    // per-path check the registrar cannot answer.
    [Fact]
    public void ABackendThatRefusesSomePaths_StillAdvertisesTheOperation()
    {
        FileSystemServerTools.SupportedToolNames(typeof(PickyBackend)).ShouldBe(["fs_read"]);
    }

    [Fact]
    public void RegisteredTools_CarryTheBackendsDescriptions()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new PickyBackend());
        services.AddMcpServer().AddFileSystemTools<PickyBackend>();
        using var provider = services.BuildServiceProvider();

        var tools = provider.GetServices<McpServerTool>().ToList();

        tools.Select(t => t.ProtocolTool.Name).ShouldBe(["fs_read"]);
        tools.Single().ProtocolTool.Description.ShouldBe(new PickyBackend().DescribeRead);
    }

    private sealed class PickyBackend : FileSystemBackendBase
    {
        public override string FilesystemName => "picky";

        public override string DescribeRead => "Reads only the one file this mount is willing to serve.";

        public override Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct) =>
            Task.FromResult(path == "allowed.md"
                ? new FsResult<FsReadResult>.Ok(new FsReadResult
                {
                    FilePath = path, Content = "", TotalLines = 0, Truncated = false
                })
                : NotFound<FsReadResult>(path));
    }
}