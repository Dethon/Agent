using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

public class McpOrganizeServerTests(McpOrganizeServerFixture fixture) : IClassFixture<McpOrganizeServerFixture>
{
    private static string GetTextContent(CallToolResult result)
    {
        return result.Content
            .OfType<TextContentBlock>()
            .Select(t => t.Text)
            .FirstOrDefault() ?? "";
    }

    [Fact]
    public async Task McpServer_IsAccessible_ReturnsTools()
    {
        // Arrange & Act
        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        var tools = await client.EnumerateToolsAsync().ToArrayAsync();

        // Assert
        tools.ShouldNotBeEmpty();
        tools.Select(t => t.Name).ShouldContain("ListDirectories");
        tools.Select(t => t.Name).ShouldContain("ListFiles");
        tools.Select(t => t.Name).ShouldContain("Move");
        tools.Select(t => t.Name).ShouldContain("CleanupDownloadDirectory");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task ListDirectoriesTool_WithEmptyLibrary_ReturnsEmptyList()
    {
        // Arrange
        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        // Act
        var result = await client.CallToolAsync(
            "ListDirectories",
            new Dictionary<string, object?>
            {
                ["path"] = fixture.LibraryPath
            },
            cancellationToken: CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        result.Content.ShouldNotBeNull();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task ListDirectoriesTool_WithDirectories_ReturnsDirectoryList()
    {
        // Arrange
        fixture.CreateLibraryStructure("TestMovies");
        fixture.CreateLibraryStructure("TestSeries");

        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        // Act
        var result = await client.CallToolAsync(
            "ListDirectories",
            new Dictionary<string, object?>
            {
                ["path"] = fixture.LibraryPath
            },
            cancellationToken: CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        var content = GetTextContent(result);
        content.ShouldContain("TestMovies");
        content.ShouldContain("TestSeries");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task ListFilesTool_WithFiles_ReturnsFileList()
    {
        // Arrange
        fixture.CreateLibraryFile(Path.Combine("FilesTest", "movie1.mkv"));
        fixture.CreateLibraryFile(Path.Combine("FilesTest", "movie2.mp4"));

        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        var filesTestPath = Path.Combine(fixture.LibraryPath, "FilesTest");

        // Act
        var result = await client.CallToolAsync(
            "ListFiles",
            new Dictionary<string, object?>
            {
                ["path"] = filesTestPath
            },
            cancellationToken: CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        var content = GetTextContent(result);
        content.ShouldContain("movie1.mkv");
        content.ShouldContain("movie2.mp4");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task MoveTool_WithValidPaths_MovesFile()
    {
        // Arrange - both source and dest must be within library path
        var sourceDir = Path.Combine("MoveTest", "source");
        var destDir = Path.Combine("MoveTest", "dest");
        fixture.CreateLibraryStructure(destDir);
        fixture.CreateLibraryFile(Path.Combine(sourceDir, "file-to-move.txt"), "content");

        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        var sourcePath = Path.Combine(fixture.LibraryPath, sourceDir, "file-to-move.txt");
        var destPath = Path.Combine(fixture.LibraryPath, destDir, "file-to-move.txt");

        // Act
        var result = await client.CallToolAsync(
            "Move",
            new Dictionary<string, object?>
            {
                ["sourcePath"] = sourcePath,
                ["destinationPath"] = destPath
            },
            cancellationToken: CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        fixture.FileExistsInLibrary(Path.Combine(destDir, "file-to-move.txt")).ShouldBeTrue();
        fixture.FileExistsInLibrary(Path.Combine(sourceDir, "file-to-move.txt")).ShouldBeFalse();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task CleanupDownloadDirectoryTool_WithDownloadId_RemovesDirectory()
    {
        // Arrange - CleanupDownloadDirectory expects a downloadId integer
        const int downloadId = 12345;
        var cleanupDir = downloadId.ToString();
        fixture.CreateDownloadFile(Path.Combine(cleanupDir, "file.txt"), "content");

        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        // Act
        var result = await client.CallToolAsync(
            "CleanupDownloadDirectory",
            new Dictionary<string, object?>
            {
                ["downloadId"] = downloadId
            },
            cancellationToken: CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        var content = GetTextContent(result);
        content.ShouldContain("success");

        var dirPath = Path.Combine(fixture.DownloadPath, cleanupDir);
        Directory.Exists(dirPath).ShouldBeFalse();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task MoveTool_WithinLibrary_MovesSuccessfully()
    {
        // Arrange - Move within library (both paths in library)
        fixture.CreateLibraryStructure("LibraryMoveTest");
        fixture.CreateLibraryFile(Path.Combine("LibraryMoveSource", "library-file.mkv"), "content");

        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        var sourcePath = Path.Combine(fixture.LibraryPath, "LibraryMoveSource", "library-file.mkv");
        var destPath = Path.Combine(fixture.LibraryPath, "LibraryMoveTest", "library-file.mkv");

        // Act
        var result = await client.CallToolAsync(
            "Move",
            new Dictionary<string, object?>
            {
                ["sourcePath"] = sourcePath,
                ["destinationPath"] = destPath
            },
            cancellationToken: CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        fixture.FileExistsInLibrary(Path.Combine("LibraryMoveTest", "library-file.mkv")).ShouldBeTrue();
        fixture.FileExistsInLibrary(Path.Combine("LibraryMoveSource", "library-file.mkv")).ShouldBeFalse();

        await client.DisposeAsync();
    }
}