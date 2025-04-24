using Infrastructure.Clients;
using Shouldly;

namespace Tests.Unit.Clients;

public class LocalFileSystemClientTests : IDisposable
{
    private readonly LocalFileSystemClient _sut;
    private readonly string _testDirectory;

    public LocalFileSystemClientTests()
    {
        _sut = new LocalFileSystemClient();
        _testDirectory = Path.Combine(Path.GetTempPath(), "LocalFileSystemClientTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [Fact]
    public async Task DescribeDirectory_WhenDirectoryExists_ReturnsFiles()
    {
        // given
        var subDir = Path.Combine(_testDirectory, "sub-dir");
        Directory.CreateDirectory(subDir);
        var file1 = Path.Combine(subDir, "file1.txt");
        var file2 = Path.Combine(subDir, "file2.txt");
        var file3 = Path.Combine(subDir, "file3.txt");
        await File.WriteAllTextAsync(file1, "content");
        await File.WriteAllTextAsync(file2, "content");
        await File.WriteAllTextAsync(file3, "content");

        // when
        var result = await _sut.DescribeDirectory(_testDirectory);

        // then
        result.ShouldNotBeEmpty();
        result.Length.ShouldBe(3);
        result.ShouldContain(file1);
        result.ShouldContain(file2);
        result.ShouldContain(file3);
    }

    [Fact]
    public async Task DescribeDirectory_WhenDirectoryDoesNotExist_ThrowsException()
    {
        // given
        var nonExistentDir = Path.Combine(_testDirectory, "does-not-exist");

        // when/then
        await Should.ThrowAsync<DirectoryNotFoundException>(() => _sut.DescribeDirectory(nonExistentDir));
    }

    [Fact]
    public async Task Move_WhenSourceFileExists_MovesSuccessfully()
    {
        // given
        var sourceFile = Path.Combine(_testDirectory, "source.txt");
        var destDir = Path.Combine(_testDirectory, "dest");
        var destFile = Path.Combine(destDir, "moved.txt");
        await File.WriteAllTextAsync(sourceFile, "test content");

        // when
        await _sut.Move(sourceFile, destFile);

        // then
        File.Exists(sourceFile).ShouldBeFalse();
        File.Exists(destFile).ShouldBeTrue();
        (await File.ReadAllTextAsync(destFile)).ShouldBe("test content");
    }

    [Fact]
    public async Task Move_WhenSourceDirectoryExists_MovesSuccessfully()
    {
        // given
        var sourceDir = Path.Combine(_testDirectory, "source-dir");
        Directory.CreateDirectory(sourceDir);
        var sourceFile = Path.Combine(sourceDir, "file.txt");
        await File.WriteAllTextAsync(sourceFile, "test content");

        var destDir = Path.Combine(_testDirectory, "dest-dir");

        // when
        await _sut.Move(sourceDir, destDir);

        // then
        Directory.Exists(sourceDir).ShouldBeFalse();
        Directory.Exists(destDir).ShouldBeTrue();
        var movedFile = Path.Combine(destDir, "file.txt");
        File.Exists(movedFile).ShouldBeTrue();
        (await File.ReadAllTextAsync(movedFile)).ShouldBe("test content");
    }

    [Fact]
    public async Task Move_WhenSourceDoesNotExist_ThrowsException()
    {
        // given
        var nonExistentFile = Path.Combine(_testDirectory, "does-not-exist.txt");
        var destFile = Path.Combine(_testDirectory, "dest.txt");

        // when/then
        await Should.ThrowAsync<IOException>(() => _sut.Move(nonExistentFile, destFile));
    }

    [Fact]
    public async Task Move_WhenDestinationParentDoesNotExist_CreatesDirectory()
    {
        // given
        var sourceFile = Path.Combine(_testDirectory, "source.txt");
        await File.WriteAllTextAsync(sourceFile, "test content");
        var destDir = Path.Combine(_testDirectory, "nested", "subfolder");
        var destFile = Path.Combine(destDir, "dest.txt");

        // when
        await _sut.Move(sourceFile, destFile);

        // then
        File.Exists(sourceFile).ShouldBeFalse();
        Directory.Exists(destDir).ShouldBeTrue();
        File.Exists(destFile).ShouldBeTrue();
    }

    [Fact]
    public async Task RemoveDirectory_WhenDirectoryExists_RemovesDirectory()
    {
        // given
        var dirToRemove = Path.Combine(_testDirectory, "remove-me");
        Directory.CreateDirectory(dirToRemove);
        await File.WriteAllTextAsync(Path.Combine(dirToRemove, "file.txt"), "content");

        // when
        await _sut.RemoveDirectory(dirToRemove);

        // then
        Directory.Exists(dirToRemove).ShouldBeFalse();
    }

    [Fact]
    public async Task RemoveDirectory_WhenDirectoryDoesNotExist_DoesNotThrow()
    {
        // given
        var nonExistentDir = Path.Combine(_testDirectory, "does-not-exist");

        // when/then
        await Should.NotThrowAsync(() => _sut.RemoveDirectory(nonExistentDir));
    }

    [Fact]
    public async Task RemoveFile_WhenFileExists_RemovesFile()
    {
        // given
        var fileToRemove = Path.Combine(_testDirectory, "remove-me.txt");
        await File.WriteAllTextAsync(fileToRemove, "content");

        // when
        await _sut.RemoveFile(fileToRemove);

        // then
        File.Exists(fileToRemove).ShouldBeFalse();
    }

    [Fact]
    public async Task RemoveFile_WhenFileDoesNotExist_DoesNotThrow()
    {
        // given
        var nonExistentFile = Path.Combine(_testDirectory, "does-not-exist.txt");

        // when/then
        await Should.NotThrowAsync(() => _sut.RemoveFile(nonExistentFile));
    }
}