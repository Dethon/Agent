using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Validation;
using Shouldly;
using Xunit;

namespace Tests.Unit.Domain.Scheduling.Vfs;

public class ScheduleFileSystemBackendTests
{
    private static ScheduleFileSystem Build() =>
        new(new FakeScheduleStore(), new FakeAgentCatalog([new ScheduleAgentInfo("jonas", "J", null)]), new CronValidator());

    [Fact]
    public void ImplementsFileSystemBackend()
    {
        Build().ShouldBeAssignableTo<IFileSystemBackend>();
    }

    [Fact]
    public void FilesystemName_IsSchedules()
    {
        Build().FilesystemName.ShouldBe("schedules");
    }

    [Fact]
    public async Task CopyAsync_IsUnsupported()
    {
        var result = await Build().CopyAsync("/jonas/a", "/jonas/b", false, true, CancellationToken.None);
        result.ShouldBeOfType<FsResult<FsCopyResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");
    }

    [Fact]
    public void ReadChunksAsync_IsUnsupported() =>
        Should.Throw<NotSupportedException>(() => { Build().ReadChunksAsync("/jonas/a/schedule.json", CancellationToken.None); });

    [Fact]
    public async Task WriteChunksAsync_IsUnsupported() =>
        await Should.ThrowAsync<NotSupportedException>(() =>
            Build().WriteChunksAsync("/jonas/a/schedule.json", AsyncEmpty(), false, true, CancellationToken.None));

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> AsyncEmpty()
    {
        await Task.CompletedTask;
        yield break;
    }
}