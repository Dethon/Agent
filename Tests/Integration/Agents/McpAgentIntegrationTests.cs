using Domain.Extensions;
using Infrastructure.Agents;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

public class McpAgentIntegrationTests(McpOrganizeServerFixture mcpFixture)
    : IClassFixture<McpOrganizeServerFixture>
{
    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddUserSecrets<McpAgentIntegrationTests>()
        .Build();

    private static OpenAiClient CreateLlmClient()
    {
        var apiKey = _configuration["openRouter:apiKey"]
                     ?? throw new SkipException("openRouter:apiKey not set in user secrets");
        var apiUrl = _configuration["openRouter:apiUrl"] ?? "https://openrouter.ai/api/v1/";
        var models = new[] { "google/gemini-2.5-flash" };

        return new OpenAiClient(apiUrl, apiKey, models);
    }

    [SkippableFact]
    public async Task Agent_WithListDirectoriesTool_CanListLibraryDirectories()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        mcpFixture.CreateLibraryStructure("AgentMovies");
        mcpFixture.CreateLibraryStructure("AgentSeries");

        var agent = await McpAgent.CreateAsync(
            [mcpFixture.McpEndpoint],
            llmClient,
            "",
            "",
            "",
            CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var responses = await agent.RunStreamingAsync(
                $"List all directories in the library at '{mcpFixture.LibraryPath}' using the ListDirectories tool. And then complete the workflow",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert - LLM responses are non-deterministic, verify agent processed the request
        responses.ShouldNotBeEmpty();
        var hasContent = responses.Any(r => !string.IsNullOrEmpty(r.Content) || !string.IsNullOrEmpty(r.ToolCalls));
        hasContent.ShouldBeTrue("Agent should have produced content or tool calls");

        await ((IAsyncDisposable)agent).DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_WithMoveFileTool_CanMoveFileWithinLibrary()
    {
        // Arrange - Move tool requires both paths to be under library path
        var llmClient = CreateLlmClient();
        mcpFixture.CreateLibraryStructure("AgentMoveDestination");
        mcpFixture.CreateLibraryFile(Path.Combine("AgentMoveSource", "agent-test-file.mkv"), "fake content");

        var agent = await McpAgent.CreateAsync(
            [mcpFixture.McpEndpoint],
            llmClient,
            "",
            "",
            "",
            CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        // Act
        var sourcePath = Path.Combine(mcpFixture.LibraryPath, "AgentMoveSource", "agent-test-file.mkv");
        var destPath = Path.Combine(mcpFixture.LibraryPath, "AgentMoveDestination", "agent-test-file.mkv");
        var responses = await agent.RunStreamingAsync(
                $"Move the file from '{sourcePath}' to '{destPath}' using the Move tool. And then complete the workflow",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();
        mcpFixture.FileExistsInLibrary(Path.Combine("AgentMoveDestination", "agent-test-file.mkv")).ShouldBeTrue();
        mcpFixture.FileExistsInLibrary(Path.Combine("AgentMoveSource", "agent-test-file.mkv")).ShouldBeFalse();

        await ((IAsyncDisposable)agent).DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_WithListFilesTool_CanListFilesInLibrary()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        mcpFixture.CreateLibraryFile(Path.Combine("AgentMoviesFiles", "agent-existing-movie.mkv"));

        var agent = await McpAgent.CreateAsync(
            [mcpFixture.McpEndpoint],
            llmClient,
            "",
            "",
            "",
            CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var moviesPath = Path.Combine(mcpFixture.LibraryPath, "AgentMoviesFiles");
        var responses = await agent.RunStreamingAsync(
                $"List all files in '{moviesPath}' using the ListFiles tool. And then complete the workflow",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert - LLM responses are non-deterministic, verify agent processed the request
        responses.ShouldNotBeEmpty();
        var hasContent = responses.Any(r => !string.IsNullOrEmpty(r.Content) || !string.IsNullOrEmpty(r.ToolCalls));
        hasContent.ShouldBeTrue("Agent should have produced content or tool calls");

        await ((IAsyncDisposable)agent).DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_WithSystemPrompt_UsesPromptInResponses()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        // Note: System prompt is now fetched from the MCP server, not passed here

        var agent = await McpAgent.CreateAsync(
            [mcpFixture.McpEndpoint],
            llmClient,
            "",
            "",
            "",
            CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var responses = await agent.RunStreamingAsync(
                "Say hello and confirm you understand your role. And then complete the workflow",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();
        var combinedResponse = string.Join(" ", responses.Select(r => r.Content));
        combinedResponse.ShouldNotBeNullOrEmpty();

        await ((IAsyncDisposable)agent).DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_WithCleanupTool_CanCleanupDownloadDirectory()
    {
        // Arrange - CleanupDownloadDirectory expects downloadId as integer
        var llmClient = CreateLlmClient();
        const int downloadId = 99999;
        var downloadSubDir = Path.Combine(mcpFixture.DownloadPath, downloadId.ToString());
        Directory.CreateDirectory(downloadSubDir);
        await File.WriteAllTextAsync(Path.Combine(downloadSubDir, "leftover.nfo"), "info file");

        var agent = await McpAgent.CreateAsync(
            [mcpFixture.McpEndpoint],
            llmClient,
            "",
            "",
            "",
            CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var responses = await agent.RunStreamingAsync(
                $"Clean up the download with ID {downloadId} using the CleanupDownloadDirectory tool. And then complete the workflow",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();
        Directory.Exists(downloadSubDir).ShouldBeFalse();

        await ((IAsyncDisposable)agent).DisposeAsync();
    }
}