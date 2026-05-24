using Infrastructure.Clients;
using Shouldly;

namespace Tests.Integration.Infrastructure;

public class LocalFileSystemClientTests : IDisposable
{
    private readonly string _testDir;
    private readonly LocalFileSystemClient _client = new();

    public LocalFileSystemClientTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"fs-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Move_WithNestedDirectories_CreatesParentDirectories()
    {
        // Arrange
        var sourceFile = Path.Combine(_testDir, "source.txt");
        var destFile = Path.Combine(_testDir, "nested", "deep", "dest.txt");
        await File.WriteAllTextAsync(sourceFile, "content");

        // Act
        await _client.Move(sourceFile, destFile);

        // Assert
        File.Exists(destFile).ShouldBeTrue();
        File.Exists(sourceFile).ShouldBeFalse();
    }

    [Fact]
    public async Task RemoveDirectory_WithContents_RemovesRecursively()
    {
        // Arrange
        var dirToRemove = Path.Combine(_testDir, "to-remove");
        Directory.CreateDirectory(Path.Combine(dirToRemove, "nested"));
        await File.WriteAllTextAsync(Path.Combine(dirToRemove, "file1.txt"), "content");
        await File.WriteAllTextAsync(Path.Combine(dirToRemove, "nested", "file2.txt"), "content");

        // Act
        await _client.RemoveDirectory(dirToRemove);

        // Assert
        Directory.Exists(dirToRemove).ShouldBeFalse();
    }

    [Fact]
    public async Task DescribeDirectory_WithFiles_ReturnsFilesByDirectory()
    {
        // Arrange
        var subDir = Path.Combine(_testDir, "subdir");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(_testDir, "root.txt"), "content");
        await File.WriteAllTextAsync(Path.Combine(subDir, "sub1.txt"), "content");
        await File.WriteAllTextAsync(Path.Combine(subDir, "sub2.txt"), "content");

        // Act
        var result = await _client.DescribeDirectory(_testDir);

        // Assert
        result.ShouldContainKey(_testDir);
        result[_testDir].ShouldContain("root.txt");
        result.ShouldContainKey(subDir);
        result[subDir].ShouldContain("sub1.txt");
        result[subDir].ShouldContain("sub2.txt");
    }

    [Fact]
    public async Task DescribeDirectory_WithNonExistentPath_ThrowsDirectoryNotFoundException()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDir, "does-not-exist");

        // Act & Assert
        await Should.ThrowAsync<DirectoryNotFoundException>(async () =>
            await _client.DescribeDirectory(nonExistentPath));
    }

    [Fact]
    public async Task Move_WithNonExistentSource_ThrowsIOException()
    {
        // Arrange
        var nonExistentSource = Path.Combine(_testDir, "does-not-exist.txt");
        var dest = Path.Combine(_testDir, "dest.txt");

        // Act & Assert
        await Should.ThrowAsync<IOException>(async () => await _client.Move(nonExistentSource, dest));
    }

    [Fact]
    public async Task Move_WithDirectory_MovesEntireDirectory()
    {
        // Arrange
        var sourceDir = Path.Combine(_testDir, "source-dir");
        var destDir = Path.Combine(_testDir, "dest-dir");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "file.txt"), "content");

        // Act
        await _client.Move(sourceDir, destDir);

        // Assert
        Directory.Exists(destDir).ShouldBeTrue();
        Directory.Exists(sourceDir).ShouldBeFalse();
        File.Exists(Path.Combine(destDir, "file.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task RemoveDirectory_WithNonExistent_DoesNotThrow()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDir, "does-not-exist");

        // Act & Assert
        await Should.NotThrowAsync(async () => await _client.RemoveDirectory(nonExistentPath));
    }

    [Fact]
    public async Task RemoveFile_WithExistingFile_RemovesFile()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "to-delete.txt");
        await File.WriteAllTextAsync(filePath, "content");

        // Act
        await _client.RemoveFile(filePath);

        // Assert
        File.Exists(filePath).ShouldBeFalse();
    }

    [Fact]
    public async Task RemoveFile_WithNonExistent_DoesNotThrow()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDir, "does-not-exist.txt");

        // Act & Assert
        await Should.NotThrowAsync(async () => await _client.RemoveFile(nonExistentPath));
    }

    [Fact]
    public async Task MoveToTrash_WithExistingFile_MovesToUserTrashFolder()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "to-trash.txt");
        await File.WriteAllTextAsync(filePath, "content");
        var trashDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            LocalFileSystemClient.TrashFolderName);

        // Act
        var trashPath = await _client.MoveToTrash(filePath);

        // Assert
        File.Exists(filePath).ShouldBeFalse();
        File.Exists(trashPath).ShouldBeTrue();
        trashPath.ShouldStartWith(trashDir);
        trashPath.ShouldContain("to-trash.txt");
        (await File.ReadAllTextAsync(trashPath)).ShouldBe("content");

        // Cleanup
        File.Delete(trashPath);
    }

    [Fact]
    public async Task MoveToTrash_WithNonExistentPath_ThrowsIOException()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testDir, "does-not-exist.txt");

        // Act & Assert
        await Should.ThrowAsync<IOException>(async () =>
            await _client.MoveToTrash(nonExistentPath));
    }

    [Fact]
    public async Task MoveToTrash_WithDirectory_MovesDirectoryToTrash()
    {
        // Arrange
        var dirPath = Path.Combine(_testDir, "to-trash-dir");
        Directory.CreateDirectory(dirPath);
        await File.WriteAllTextAsync(Path.Combine(dirPath, "file.txt"), "content");
        var trashDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            LocalFileSystemClient.TrashFolderName);

        // Act
        var trashPath = await _client.MoveToTrash(dirPath);

        // Assert
        Directory.Exists(dirPath).ShouldBeFalse();
        Directory.Exists(trashPath).ShouldBeTrue();
        trashPath.ShouldStartWith(trashDir);
        trashPath.ShouldContain("to-trash-dir");
        File.Exists(Path.Combine(trashPath, "file.txt")).ShouldBeTrue();

        // Cleanup
        Directory.Delete(trashPath, true);
    }

    [Fact]
    public async Task MoveToTrash_WithMultipleFiles_CreatesUniqueTrashPaths()
    {
        // Arrange
        var file1 = Path.Combine(_testDir, "same-name.txt");
        var file2 = Path.Combine(_testDir, "subdir", "same-name.txt");
        Directory.CreateDirectory(Path.Combine(_testDir, "subdir"));
        await File.WriteAllTextAsync(file1, "content1");
        await File.WriteAllTextAsync(file2, "content2");

        // Act
        var trashPath1 = await _client.MoveToTrash(file1);
        var trashPath2 = await _client.MoveToTrash(file2);

        // Assert
        trashPath1.ShouldNotBe(trashPath2);
        File.Exists(trashPath1).ShouldBeTrue();
        File.Exists(trashPath2).ShouldBeTrue();

        // Cleanup
        File.Delete(trashPath1);
        File.Delete(trashPath2);
    }

    [Fact]
    public async Task Glob_WithFileWildcard_ReturnsMatchingFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_testDir, "movie.mkv"), "content");
        await File.WriteAllTextAsync(Path.Combine(_testDir, "movie.mp4"), "content");
        await File.WriteAllTextAsync(Path.Combine(_testDir, "readme.txt"), "content");

        var hits = await _client.Glob(_testDir, "*.mkv");

        hits.Length.ShouldBe(1);
        hits[0].ShouldEndWith("movie.mkv");
    }

    [Fact]
    public async Task Glob_NoTrailingSlash_ReturnsFilesAndDirectoriesWithDirsMarked()
    {
        var subDir = Path.Combine(_testDir, "movies");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(_testDir, "todo.md"), "content");

        var hits = await _client.Glob(_testDir, "*");

        hits.ShouldContain(h => h.EndsWith("todo.md") && !h.EndsWith("/"));
        hits.ShouldContain(h => h.EndsWith("movies/"));
    }

    [Fact]
    public async Task Glob_WithTrailingSlash_ReturnsDirectoriesOnly()
    {
        var subDir = Path.Combine(_testDir, "movies");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(_testDir, "todo.md"), "content");

        var hits = await _client.Glob(_testDir, "*/");

        hits.Length.ShouldBe(1);
        hits[0].ShouldEndWith("movies/");
    }

    [Fact]
    public async Task Glob_RecursiveDirsOnly_ReturnsNestedDirectoriesMarked()
    {
        var deep = Path.Combine(_testDir, "movies", "action");
        Directory.CreateDirectory(deep);
        await File.WriteAllTextAsync(Path.Combine(deep, "film.mkv"), "content");

        var hits = await _client.Glob(_testDir, "**/");

        hits.ShouldContain(h => h.EndsWith("movies/"));
        hits.ShouldContain(h => h.EndsWith("action/"));
        hits.ShouldAllBe(h => h.EndsWith("/"));
    }

    [Fact]
    public async Task Glob_ExcludesRootDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_testDir, "sub"));

        var hits = await _client.Glob(_testDir, "**/");

        hits.ShouldNotContain(h => h.TrimEnd('/') == _testDir);
    }

    [Fact]
    public async Task Glob_RecursiveFilePattern_ReturnsNestedFiles()
    {
        var subDir = Path.Combine(_testDir, "sub", "deep");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(_testDir, "root.txt"), "content");
        await File.WriteAllTextAsync(Path.Combine(subDir, "nested.txt"), "content");

        var hits = await _client.Glob(_testDir, "**/*.txt");

        hits.Length.ShouldBe(2);
        hits.ShouldContain(h => h.EndsWith("root.txt"));
        hits.ShouldContain(h => h.EndsWith("nested.txt"));
    }

    [Fact]
    public async Task Glob_WithNoMatches_ReturnsEmptyArray()
    {
        await File.WriteAllTextAsync(Path.Combine(_testDir, "file.txt"), "content");

        var hits = await _client.Glob(_testDir, "*.pdf");

        hits.ShouldBeEmpty();
    }

    [Fact]
    public async Task Glob_ReturnsAbsolutePaths()
    {
        await File.WriteAllTextAsync(Path.Combine(_testDir, "file.txt"), "content");

        var hits = await _client.Glob(_testDir, "**/*");

        hits.ShouldAllBe(h => h.StartsWith(_testDir));
    }
}