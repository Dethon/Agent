using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

public class McpLibraryServerTests(McpLibraryServerFixture fixture) : IClassFixture<McpLibraryServerFixture>
{
    private static string GetTextContent(CallToolResult result)
    {
        return result.Content
            .OfType<TextContentBlock>()
            .Select(t => t.Text)
            .FirstOrDefault() ?? "";
    }

    [Fact]
    public async Task McpServer_IsAccessible_ReturnsAllTools()
    {
        // Arrange & Act
        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        var tools = await client.ListToolsAsync();

        // Assert
        tools.ShouldNotBeEmpty();
        var toolNames = tools.Select(t => t.Name).ToList();

        // Download tools
        toolNames.ShouldContain("FileSearch");
        toolNames.ShouldContain("FileDownload");
        toolNames.ShouldContain("GetDownloadStatus");
        toolNames.ShouldContain("CleanupDownload");

        // Library organization tools
        toolNames.ShouldContain("ListDirectories");
        toolNames.ShouldContain("ListFiles");
        toolNames.ShouldContain("Move");

        await client.DisposeAsync();
    }

    #region FileSearch Tests

    [Fact]
    public async Task FileSearchTool_WithQuery_ReturnsResults()
    {
        // Arrange
        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        // Act - search for something generic that Jackett might return results for
        var result = await client.CallToolAsync(
            "FileSearch",
            new Dictionary<string, object?>
            {
                ["searchStrings"] = new[] { "test" }
            },
            cancellationToken: CancellationToken.None);

        // Assert - we can't guarantee results from Jackett without configured indexers,
        // but the tool should execute without error
        result.ShouldNotBeNull();
        var content = GetTextContent(result);
        content.ShouldContain("status");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task FileSearchTool_WithEmptyQuery_ReturnsEmptyResults()
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
            "FileSearch",
            new Dictionary<string, object?>
            {
                ["searchStrings"] = Array.Empty<string>()
            },
            cancellationToken: CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        var content = GetTextContent(result);
        content.ShouldContain("success");
        content.ShouldContain("totalResults");

        await client.DisposeAsync();
    }

    #endregion

    #region FileDownload Tests

    [Fact]
    public async Task FileDownloadTool_WithInvalidId_ReturnsError()
    {
        // Arrange
        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        // Act - try to download with an ID that doesn't exist in search results
        var result = await client.CallToolAsync(
            "FileDownload",
            new Dictionary<string, object?>
            {
                ["searchResultId"] = 12345
            },
            cancellationToken: CancellationToken.None);

        // Assert - should return error because no search was performed first
        result.ShouldNotBeNull();
        var content = GetTextContent(result);
        content.ShouldContain(
            "No search result found for id 12345. Make sure to run the FileSearch tool first and use the correct");

        await client.DisposeAsync();
    }

    #endregion

    #region GetDownloadStatus Tests

    [Fact]
    public async Task GetDownloadStatusTool_WithMissingDownload_ReturnsMissing()
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
            "GetDownloadStatus",
            new Dictionary<string, object?>
            {
                ["downloadId"] = 99999
            },
            cancellationToken: CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        var content = GetTextContent(result);
        content.ShouldContain("mising"); // Note: typo exists in original code

        await client.DisposeAsync();
    }

    #endregion

    #region CleanupDownload Tests

    [Fact]
    public async Task CleanupDownloadTool_WithNonExistentDownload_Succeeds()
    {
        // Arrange
        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        // Act - cleanup a non-existent download should still succeed
        var result = await client.CallToolAsync(
            "CleanupDownload",
            new Dictionary<string, object?>
            {
                ["downloadId"] = 99999
            },
            cancellationToken: CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        var content = GetTextContent(result);
        content.ShouldContain("success");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task CleanupDownloadTool_WithDownloadId_RemovesDirectory()
    {
        // Arrange - CleanupDownload expects a downloadId integer
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
            "CleanupDownload",
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

    #endregion

    #region ListDirectories Tests

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

    #endregion

    #region ListFiles Tests

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

    #endregion

    #region Move Tests

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

    #endregion
}
