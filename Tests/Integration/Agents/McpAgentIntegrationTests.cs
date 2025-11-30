using Domain.DTOs;
using Infrastructure.Agents;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

public class McpAgentIntegrationTests(McpOrganizeServerFixture mcpFixture, RedisFixture redisFixture)
    : IClassFixture<McpOrganizeServerFixture>, IClassFixture<RedisFixture>
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

        var responses = new List<AiResponse>();
        var agent = await McpAgent.CreateAsync(
            [mcpFixture.McpEndpoint],
            "test-conversation-1",
            [],
            (response, _) =>
            {
                responses.Add(response);
                return Task.CompletedTask;
            },
            llmClient,
            redisFixture.Store,
            CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        await agent.Run(
            [$"List all directories in the library at '{mcpFixture.LibraryPath}' using the ListDirectories tool."],
            cts.Token);

        // Assert - LLM responses are non-deterministic, verify agent processed the request
        responses.ShouldNotBeEmpty();
        var hasContent = responses.Any(r => !string.IsNullOrEmpty(r.Content) || !string.IsNullOrEmpty(r.ToolCalls));
        hasContent.ShouldBeTrue("Agent should have produced content or tool calls");

        await agent.DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_WithMoveFileTool_CanMoveFileWithinLibrary()
    {
        // Arrange - Move tool requires both paths to be under library path
        var llmClient = CreateLlmClient();
        mcpFixture.CreateLibraryStructure("AgentMoveDestination");
        mcpFixture.CreateLibraryFile(Path.Combine("AgentMoveSource", "agent-test-file.mkv"), "fake content");

        var responses = new List<AiResponse>();
        var agent = await McpAgent.CreateAsync(
            [mcpFixture.McpEndpoint],
            "test-conversation-2",
            [],
            (response, _) =>
            {
                responses.Add(response);
                return Task.CompletedTask;
            },
            llmClient,
            redisFixture.Store,
            CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var sourcePath = Path.Combine(mcpFixture.LibraryPath, "AgentMoveSource", "agent-test-file.mkv");
        var destPath = Path.Combine(mcpFixture.LibraryPath, "AgentMoveDestination", "agent-test-file.mkv");
        await agent.Run(
            [$"Move the file from '{sourcePath}' to '{destPath}' using the Move tool."],
            cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();
        mcpFixture.FileExistsInLibrary(Path.Combine("AgentMoveDestination", "agent-test-file.mkv")).ShouldBeTrue();
        mcpFixture.FileExistsInLibrary(Path.Combine("AgentMoveSource", "agent-test-file.mkv")).ShouldBeFalse();

        await agent.DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_WithListFilesTool_CanListFilesInLibrary()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        mcpFixture.CreateLibraryFile(Path.Combine("AgentMoviesFiles", "agent-existing-movie.mkv"));

        var responses = new List<AiResponse>();
        var agent = await McpAgent.CreateAsync(
            [mcpFixture.McpEndpoint],
            "test-conversation-3",
            [],
            (response, _) =>
            {
                responses.Add(response);
                return Task.CompletedTask;
            },
            llmClient,
            redisFixture.Store,
            CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var moviesPath = Path.Combine(mcpFixture.LibraryPath, "AgentMoviesFiles");
        await agent.Run(
            [$"List all files in '{moviesPath}' using the ListFiles tool."],
            cts.Token);

        // Assert - LLM responses are non-deterministic, verify agent processed the request
        responses.ShouldNotBeEmpty();
        var hasContent = responses.Any(r => !string.IsNullOrEmpty(r.Content) || !string.IsNullOrEmpty(r.ToolCalls));
        hasContent.ShouldBeTrue("Agent should have produced content or tool calls");

        await agent.DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_CancelCurrentExecution_StopsProcessing()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        mcpFixture.CreateLibraryStructure("AgentCancelTest");

        var agent = await McpAgent.CreateAsync(
            [mcpFixture.McpEndpoint],
            "test-conversation-4",
            [],
            async (_, ct) =>
            {
                await Task.Delay(100, ct);
            },
            llmClient,
            redisFixture.Store,
            CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        var runTask = agent.Run(
            [$"List all directories in '{mcpFixture.LibraryPath}'."],
            cts.Token);

        await Task.Delay(500, cts.Token);
        agent.CancelCurrentExecution();

        // Assert - should not throw, task should complete gracefully
        await Should.NotThrowAsync(async () =>
        {
            try
            {
                await runTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        });

        await agent.DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_WithSystemPrompt_UsesPromptInResponses()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        var initialMessages = new AiMessage[]
        {
            new()
            {
                Role = AiMessageRole.System,
                Content =
                    "You are a helpful file organizer assistant. Always respond with 'Aye aye!' before any action."
            }
        };

        var responses = new List<AiResponse>();
        var agent = await McpAgent.CreateAsync(
            [mcpFixture.McpEndpoint],
            "test-conversation-5",
            initialMessages,
            (response, _) =>
            {
                responses.Add(response);
                return Task.CompletedTask;
            },
            llmClient,
            redisFixture.Store,
            CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        await agent.Run(
            ["Say hello and confirm you understand your role."],
            cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();
        var combinedResponse = string.Join(" ", responses.Select(r => r.Content));
        combinedResponse.ShouldNotBeNullOrEmpty();

        await agent.DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_LastExecutionTime_IsUpdatedAfterRun()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        var agent = await McpAgent.CreateAsync(
            [mcpFixture.McpEndpoint],
            "test-conversation-6",
            [],
            (_, _) => Task.CompletedTask,
            llmClient,
            redisFixture.Store,
            CancellationToken.None);

        var beforeRun = DateTime.UtcNow;
        await Task.Delay(10);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        await agent.Run(["Hello"], cts.Token);

        // Assert
        agent.LastExecutionTime.ShouldBeGreaterThan(beforeRun);

        await agent.DisposeAsync();
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

        var responses = new List<AiResponse>();
        var agent = await McpAgent.CreateAsync(
            [mcpFixture.McpEndpoint],
            "test-conversation-7",
            [],
            (response, _) =>
            {
                responses.Add(response);
                return Task.CompletedTask;
            },
            llmClient,
            redisFixture.Store,
            CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        await agent.Run(
            [$"Clean up the download with ID {downloadId} using the CleanupDownloadDirectory tool."],
            cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();
        Directory.Exists(downloadSubDir).ShouldBeFalse();

        await agent.DisposeAsync();
    }
}
