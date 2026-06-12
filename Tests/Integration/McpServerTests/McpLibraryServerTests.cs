using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.Channel;
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

        // Filesystem backend tools
        toolNames.ShouldContain("fs_glob");
        toolNames.ShouldContain("fs_move");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task McpServer_ExposesSingleMediaFilesystemResource()
    {
        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        var resources = await client.ListResourcesAsync(cancellationToken: CancellationToken.None);
        var fsResources = resources.Where(r => r.Uri.StartsWith("filesystem://")).ToList();

        fsResources.ShouldHaveSingleItem().Uri.ShouldBe("filesystem://media");

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
    public async Task DownloadFile_WithConversationContextMeta_RecordsRoutingAndServesMediaOverlay()
    {
        // Arrange
        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        var downloadTool = (await client.ListToolsAsync()).Single(t => t.Name == "download_file");
        var context = new ConversationContext("jack", "conv-journey", "fran", new ReplyTarget("signalr", "conv-journey"));
        var meta = new JsonObject
        {
            ["conversationContext"] = JsonSerializer.SerializeToNode(context, ChannelProtocol.SerializerOptions)
        };
        const string magnetLink =
            "magnet:?xt=urn:btih:KRWPCX3SJUM4IMM4YF3MVSJIBFTHVFCS&dn=ubuntu-24.04-desktop-amd64.iso";

        // Act - download_file with _meta carrying the conversation context
        var downloadResult = await downloadTool.WithMeta(meta).CallAsync(
            new Dictionary<string, object?>
            {
                ["searchResultId"] = null,
                ["link"] = magnetLink,
                ["title"] = "Journey Test"
            },
            cancellationToken: CancellationToken.None);

        // Assert - the routing snapshot points back at the origin conversation
        GetTextContent(downloadResult).ShouldContain("success");
        var routing = (await fixture.RoutingStore.ListAsync()).ShouldHaveSingleItem();
        routing.Title.ShouldBe("Journey Test");
        routing.Context.ConversationId.ShouldBe("conv-journey");
        routing.Context.Origin.ChannelId.ShouldBe("signalr");
        var id = routing.DownloadId;

        // Assert - the download is visible through the media filesystem's downloads overlay
        var globResult = await client.CallToolAsync(
            "fs_glob",
            new Dictionary<string, object?>
            {
                ["pattern"] = "**",
                ["basePath"] = "downloads",
                ["filesystem"] = "media"
            },
            cancellationToken: CancellationToken.None);
        GetTextContent(globResult).ShouldContain($"downloads/{id}/status.json");

        var readResult = await client.CallToolAsync(
            "fs_read",
            new Dictionary<string, object?>
            {
                ["path"] = $"downloads/{id}/status.json",
                ["filesystem"] = "media"
            },
            cancellationToken: CancellationToken.None);
        GetTextContent(readResult).ShouldContain(id.ToString());

        // Assert - the removed 'downloads' filesystem name is rejected
        var staleResult = await client.CallToolAsync(
            "fs_read",
            new Dictionary<string, object?>
            {
                ["path"] = $"downloads/{id}/status.json",
                ["filesystem"] = "downloads"
            },
            cancellationToken: CancellationToken.None);
        GetTextContent(staleResult).ShouldContain("unsupported_operation");

        // Act - deleting the download dir cancels the torrent and drops the routing entry
        var deleteResult = await client.CallToolAsync(
            "fs_delete",
            new Dictionary<string, object?>
            {
                ["path"] = $"downloads/{id}",
                ["filesystem"] = "media"
            },
            cancellationToken: CancellationToken.None);

        // Assert
        GetTextContent(deleteResult).ShouldContain("removed");
        (await fixture.RoutingStore.ListAsync()).ShouldBeEmpty();

        await client.DisposeAsync();
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

    [Theory]
    [InlineData("MoveTest/source", "MoveTest/dest", "file-to-move.txt", "file-to-move.txt")]
    [InlineData("LibraryMoveSource", "LibraryMoveTest", "library-file.mkv", "library-file.mkv")]
    public async Task MoveTool_WithinLibrary_MovesFile(
        string srcDir, string dstDir, string srcFileName, string dstFileName)
    {
        // Arrange - both source and dest must be within library path
        fixture.CreateLibraryStructure(dstDir);
        fixture.CreateLibraryFile(Path.Combine(srcDir, srcFileName), "content");

        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        var sourcePath = Path.Combine(fixture.LibraryPath, srcDir, srcFileName);
        var destPath = Path.Combine(fixture.LibraryPath, dstDir, dstFileName);

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
        fixture.FileExistsInLibrary(Path.Combine(dstDir, dstFileName)).ShouldBeTrue();
        fixture.FileExistsInLibrary(Path.Combine(srcDir, srcFileName)).ShouldBeFalse();

        await client.DisposeAsync();
    }

    #endregion
}