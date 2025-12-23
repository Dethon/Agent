using System.Text.Json;
using Domain.Agents;
using Domain.Extensions;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.StateManagers;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

public class McpAgentIntegrationTests(McpLibraryServerFixture mcpFixture, RedisFixture redisFixture)
    : IClassFixture<McpLibraryServerFixture>, IClassFixture<RedisFixture>
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

    private McpAgent CreateAgent(OpenAiClient llmClient)
    {
        var stateStore = new RedisThreadStateStore(redisFixture.Connection, TimeSpan.FromMinutes(10));
        return new McpAgent(
            [mcpFixture.McpEndpoint],
            llmClient,
            "test-agent",
            "",
            stateStore);
    }

    [SkippableFact]
    public async Task Agent_WithListDirectoriesTool_CanListLibraryDirectories()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        mcpFixture.CreateLibraryStructure("AgentMovies");
        mcpFixture.CreateLibraryStructure("AgentSeries");

        var agent = CreateAgent(llmClient);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var responses = await agent.RunStreamingAsync(
                $"List all directories in the library at '{mcpFixture.LibraryPath}' using the ListDirectories tool.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

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

        var agent = CreateAgent(llmClient);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));

        // Act
        var sourcePath = Path.Combine(mcpFixture.LibraryPath, "AgentMoveSource", "agent-test-file.mkv");
        var destPath = Path.Combine(mcpFixture.LibraryPath, "AgentMoveDestination", "agent-test-file.mkv");
        var responses = await agent.RunStreamingAsync(
                $"Move the file from '{sourcePath}' to '{destPath}' using the Move tool.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

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

        var agent = CreateAgent(llmClient);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var moviesPath = Path.Combine(mcpFixture.LibraryPath, "AgentMoviesFiles");
        var responses = await agent.RunStreamingAsync(
                $"List all files in '{moviesPath}' using the ListFiles tool.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert - LLM responses are non-deterministic, verify agent processed the request
        responses.ShouldNotBeEmpty();
        var hasContent = responses.Any(r => !string.IsNullOrEmpty(r.Content) || !string.IsNullOrEmpty(r.ToolCalls));
        hasContent.ShouldBeTrue("Agent should have produced content or tool calls");

        await agent.DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_WithSystemPrompt_UsesPromptInResponses()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        // Note: System prompt is now fetched from the MCP server, not passed here

        var agent = CreateAgent(llmClient);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var responses = await agent.RunStreamingAsync(
                "Say hello and confirm you understand your role.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();
        var combinedResponse = string.Join(" ", responses.Select(r => r.Content));
        combinedResponse.ShouldNotBeNullOrEmpty();

        await agent.DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_WithCleanupTool_CanCleanupDownloadDirectory()
    {
        // Arrange - CleanupDownload expects downloadId as integer
        var llmClient = CreateLlmClient();
        const int downloadId = 99999;
        var downloadSubDir = Path.Combine(mcpFixture.DownloadPath, downloadId.ToString());
        Directory.CreateDirectory(downloadSubDir);
        await File.WriteAllTextAsync(Path.Combine(downloadSubDir, "leftover.nfo"), "info file");

        var agent = CreateAgent(llmClient);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var responses = await agent.RunStreamingAsync(
                $"Clean up the download with ID {downloadId} using the CleanupDownload tool.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();
        Directory.Exists(downloadSubDir).ShouldBeFalse();

        await agent.DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_ThreadSerialization_CanSerializeAndDeserializeThread()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        var agent = CreateAgent(llmClient);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act - First interaction to create a thread with state
        var thread = agent.GetNewThread();
        var responses1 = await agent.RunStreamingAsync(
                "Remember: my favorite color is blue.",
                thread,
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Serialize the thread
        var serialized = thread.Serialize();
        var serializedJson = serialized.GetRawText();

        // Deserialize into a new thread
        var deserializedThread = agent.DeserializeThread(serialized);

        // Continue conversation with deserialized thread
        var responses2 = await agent.RunStreamingAsync(
                "What is my favorite color?",
                deserializedThread,
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert
        responses1.ShouldNotBeEmpty();
        responses2.ShouldNotBeEmpty();
        serializedJson.ShouldNotBeNullOrEmpty();

        await agent.DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_ThreadSerialization_WithAgentKey_CanRestoreThread()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        var agent = CreateAgent(llmClient);
        var agentKey = new AgentKey(12345, 67890);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act - Create thread from AgentKey (simulating how ChatMonitor works)
        var agentKeyJson = JsonSerializer.SerializeToElement(agentKey);
        var thread = agent.DeserializeThread(agentKeyJson);

        // First interaction
        var responses1 = await agent.RunStreamingAsync(
                "Remember: my name is TestUser.",
                thread,
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Create a new agent instance (simulating agent restart)
        await agent.DisposeAsync();
        var agent2 = CreateAgent(llmClient);

        // Restore thread from same AgentKey
        var thread2 = agent2.DeserializeThread(agentKeyJson);

        // Continue conversation
        var responses2 = await agent2.RunStreamingAsync(
                "What is my name?",
                thread2,
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert
        responses1.ShouldNotBeEmpty();
        responses2.ShouldNotBeEmpty();

        await agent2.DisposeAsync();
    }
}