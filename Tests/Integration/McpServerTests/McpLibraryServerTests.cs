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
        toolNames.ShouldContain("file_search");
        toolNames.ShouldContain("download_file");
        toolNames.ShouldContain("download_status");
        toolNames.ShouldContain("download_cleanup");

        // Filesystem backend tools
        toolNames.ShouldContain("fs_glob");
        toolNames.ShouldContain("fs_move");

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
            "file_search",
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
            "download_file",
            new Dictionary<string, object?>
            {
                ["searchResultId"] = 12345,
                ["link"] = null,
                ["title"] = null
            },
            cancellationToken: CancellationToken.None);

        // Assert - should return error because no search was performed first
        result.ShouldNotBeNull();
        var content = GetTextContent(result);
        content.ShouldContain(
            "No search result found for id 12345. Make sure to run the file_search tool first and use the correct");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task FileDownloadTool_WithLinkAndTitle_StartsDownloadAndStatusReportsTitle()
    {
        // Arrange — use a small, well-seeded public-domain magnet for stability.
        // Sintel (Blender Foundation, Creative Commons) is a long-standing test torrent.
        const string link =
            "magnet:?xt=urn:btih:08ada5a7a6183aae1e09d831df6748d566095a10" +
            "&dn=Sintel" +
            "&tr=udp%3A%2F%2Fexplodie.org%3A6969" +
            "&tr=udp%3A%2F%2Ftracker.coppersurfer.tk%3A6969" +
            "&tr=udp%3A%2F%2Ftracker.empire-js.us%3A1337" +
            "&tr=udp%3A%2F%2Ftracker.leechers-paradise.org%3A6969" +
            "&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337" +
            "&tr=wss%3A%2F%2Ftracker.btorrent.xyz" +
            "&tr=wss%3A%2F%2Ftracker.fastcast.nz" +
            "&tr=wss%3A%2F%2Ftracker.openwebtorrent.com" +
            "&ws=https%3A%2F%2Fwebtorrent.io%2Ftorrents%2F";
        const string title = "Sintel Test Title";
        var expectedId = link.GetHashCode();

        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        try
        {
            // Act — start download via the link path
            var downloadResult = await client.CallToolAsync(
                "download_file",
                new Dictionary<string, object?>
                {
                    ["searchResultId"] = null,
                    ["link"] = link,
                    ["title"] = title
                },
                cancellationToken: CancellationToken.None);

            // Assert — download_file accepted the link and returned success
            downloadResult.ShouldNotBeNull();
            var downloadContent = GetTextContent(downloadResult);
            downloadContent.ShouldContain("success");

            // Act — query status using the same id
            var statusResult = await client.CallToolAsync(
                "download_status",
                new Dictionary<string, object?>
                {
                    ["downloadId"] = expectedId
                },
                cancellationToken: CancellationToken.None);

            // Assert — status carries our supplied title (came from synthetic SearchResult cache)
            var statusContent = GetTextContent(statusResult);
            statusContent.ShouldContain(title);

            // Cleanup — verify the same id flows through cleanup
            var cleanupResult = await client.CallToolAsync(
                "download_cleanup",
                new Dictionary<string, object?>
                {
                    ["downloadId"] = expectedId
                },
                cancellationToken: CancellationToken.None);

            var cleanupContent = GetTextContent(cleanupResult);
            cleanupContent.ShouldContain("success");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task FileDownloadTool_WithBothIdAndLink_ReturnsInvalidArgument()
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
            "download_file",
            new Dictionary<string, object?>
            {
                ["searchResultId"] = 1,
                ["link"] = "magnet:?xt=urn:btih:x",
                ["title"] = "x"
            },
            cancellationToken: CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        var content = GetTextContent(result);
        content.ShouldContain("invalid_argument");

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
            "download_status",
            new Dictionary<string, object?>
            {
                ["downloadId"] = 99999
            },
            cancellationToken: CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        var content = GetTextContent(result);
        content.ShouldContain("missing");

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
            "download_cleanup",
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
            "download_cleanup",
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

    #region GlobFiles Tests

    [Fact]
    public async Task GlobFilesTool_WithMatchingFiles_ReturnsFileList()
    {
        // Arrange
        fixture.CreateLibraryFile(Path.Combine("GlobTest", "movie1.mkv"));
        fixture.CreateLibraryFile(Path.Combine("GlobTest", "movie2.mkv"));
        fixture.CreateLibraryFile(Path.Combine("GlobTest", "readme.txt"));

        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        // Act
        var result = await client.CallToolAsync(
            "fs_glob",
            new Dictionary<string, object?>
            {
                ["pattern"] = "**/*.mkv",
                ["mode"] = "files",
                ["basePath"] = "GlobTest"
            },
            cancellationToken: CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        var content = GetTextContent(result);
        content.ShouldContain("movie1.mkv");
        content.ShouldContain("movie2.mkv");
        content.ShouldNotContain("readme.txt");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task GlobFilesTool_WithRecursivePattern_FindsNestedFiles()
    {
        // Arrange
        fixture.CreateLibraryFile(Path.Combine("GlobDeep", "sub1", "file.txt"));
        fixture.CreateLibraryFile(Path.Combine("GlobDeep", "sub2", "nested", "deep.txt"));

        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        // Act
        var result = await client.CallToolAsync(
            "fs_glob",
            new Dictionary<string, object?>
            {
                ["pattern"] = "**/*.txt",
                ["mode"] = "files",
                ["basePath"] = "GlobDeep"
            },
            cancellationToken: CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        var content = GetTextContent(result);
        content.ShouldContain("file.txt");
        content.ShouldContain("deep.txt");

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
            "fs_move",
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
            "fs_move",
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