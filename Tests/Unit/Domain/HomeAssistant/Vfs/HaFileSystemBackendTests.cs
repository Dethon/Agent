using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaFileSystemBackendTests
{
    private static HaFileSystem Build()
    {
        var client = new FakeHaClient();
        return new HaFileSystem(new HaCatalogProvider(() => client, new FakeTimeProvider()), () => client);
    }

    [Fact]
    public void ImplementsFileSystemBackend() => Build().ShouldBeAssignableTo<IFileSystemBackend>();

    [Fact]
    public void FilesystemName_IsHa() => Build().FilesystemName.ShouldBe("ha");

    [Fact]
    public async Task CreateAsync_IsUnsupported()
    {
        var result = await Build().CreateAsync("entities/light/x/state.json", "{}", false, true, CancellationToken.None);
        result.ShouldBeOfType<FsResult<FsCreateResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");
    }

    [Fact]
    public async Task EditAsync_IsUnsupported()
    {
        var result = await Build().EditAsync("entities/light/x/state.json", [], CancellationToken.None);
        result.ShouldBeOfType<FsResult<FsEditResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");
    }

    [Fact]
    public async Task MoveAsync_IsUnsupported()
    {
        var result = await Build().MoveAsync("entities/light/a", "entities/light/b", CancellationToken.None);
        result.ShouldBeOfType<FsResult<FsMoveResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");
    }

    [Fact]
    public async Task DeleteAsync_IsUnsupported()
    {
        var result = await Build().DeleteAsync("entities/light/x", CancellationToken.None);
        result.ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");
    }

    [Fact]
    public async Task CopyAsync_IsUnsupported()
    {
        var result = await Build().CopyAsync("entities/light/a", "entities/light/b", false, true, CancellationToken.None);
        result.ShouldBeOfType<FsResult<FsCopyResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");
    }

    [Fact]
    public void ReadChunksAsync_IsUnsupported() =>
        Should.Throw<NotSupportedException>(() => { Build().ReadChunksAsync("entities/light/x/state.json", CancellationToken.None); });

    [Fact]
    public async Task WriteChunksAsync_IsUnsupported() =>
        await Should.ThrowAsync<NotSupportedException>(() =>
            Build().WriteChunksAsync("entities/light/x/state.json", AsyncEmpty(), false, true, CancellationToken.None));

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> AsyncEmpty()
    {
        await Task.CompletedTask;
        yield break;
    }
}