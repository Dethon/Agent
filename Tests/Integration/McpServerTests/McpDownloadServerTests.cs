using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

public class McpDownloadServerTests(McpDownloadServerFixture fixture) : IClassFixture<McpDownloadServerFixture>
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
        tools.Select(t => t.Name).ShouldContain("FileSearch");
        tools.Select(t => t.Name).ShouldContain("FileDownload");
        tools.Select(t => t.Name).ShouldContain("GetDownloadStatus");
        tools.Select(t => t.Name).ShouldContain("CleanupDownloadTask");

        await client.DisposeAsync();
    }

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
            "CleanupDownloadTask",
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
}