using Domain.Extensions;
using Domain.Tools.FileSystem;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.StateManagers;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

public class McpAgentMultiFileSystemTests(MultiFileSystemFixture fsFixture, RedisFixture redisFixture)
    : IClassFixture<MultiFileSystemFixture>, IClassFixture<RedisFixture>
{
    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddUserSecrets<McpAgentMultiFileSystemTests>()
        .Build();

    private static readonly IReadOnlySet<string> _allFileSystemTools = FileSystemToolFeature.AllToolKeys;

    private static OpenRouterChatClient CreateLlmClient()
    {
        var apiKey = _configuration["openRouter:apiKey"]
                     ?? throw new SkipException("openRouter:apiKey not set in user secrets");
        var apiUrl = _configuration["openRouter:apiUrl"] ?? "https://openrouter.ai/api/v1/";

        return new OpenRouterChatClient(apiUrl, apiKey, "z-ai/glm-4.7-flash");
    }

    private McpAgent CreateAgent(OpenRouterChatClient llmClient)
    {
        var stateStore = new RedisThreadStateStore(redisFixture.Connection, TimeSpan.FromMinutes(10));
        return new McpAgent(
            [fsFixture.LibraryEndpoint, fsFixture.NotesEndpoint],
            llmClient,
            "test-multi-fs-agent",
            "",
            stateStore,
            "test-user",
            filesystemEnabledTools: _allFileSystemTools);
    }

    [SkippableFact]
    public async Task Agent_WithMultipleFileSystems_CanReadFromBoth()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        fsFixture.CreateLibraryFile("multi-read.md", "Library content alpha");
        fsFixture.CreateNotesFile("multi-read.md", "Notes content bravo");

        var agent = CreateAgent(llmClient);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var responses = await agent.RunStreamingAsync(
                "Read both /library/multi-read.md and /notes/multi-read.md using the domain:filesystem:text_read tool. " +
                "Tell me the content of each file.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();
        var combined = string.Join(" ", responses.Select(r => r.Content));
        combined.ShouldContain("alpha");
        combined.ShouldContain("bravo");

        await agent.DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_WithMultipleFileSystems_CanCreateOnEach()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        var agent = CreateAgent(llmClient);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var responses = await agent.RunStreamingAsync(
                "Create two files:\n" +
                "1. /library/multi-create.md with content 'library file'\n" +
                "2. /notes/multi-create.md with content 'notes file'\n" +
                "Use the domain:filesystem:text_create tool for each.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();

        var libraryFile = Path.Combine(fsFixture.LibraryPath, "multi-create.md");
        File.Exists(libraryFile).ShouldBeTrue("File should exist in library filesystem");
        (await File.ReadAllTextAsync(libraryFile)).ShouldContain("library");

        var notesFile = Path.Combine(fsFixture.NotesPath, "multi-create.md");
        File.Exists(notesFile).ShouldBeTrue("File should exist in notes filesystem");
        (await File.ReadAllTextAsync(notesFile)).ShouldContain("notes");

        await agent.DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_WithMultipleFileSystems_KnowsAvailableMountPoints()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        var agent = CreateAgent(llmClient);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var responses = await agent.RunStreamingAsync(
                "What filesystems are available to you? List all mount points.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();
        var combined = string.Join(" ", responses.Select(r => r.Content)).ToLowerInvariant();
        combined.ShouldContain("/library");
        combined.ShouldContain("/notes");

        await agent.DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_WithMultipleFileSystems_CanSearchAcrossFileSystems()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        fsFixture.CreateLibraryFile("search-multi.md", "The unicorn galloped through the meadow.");
        fsFixture.CreateNotesFile("search-multi.md", "No magical creatures here.");

        var agent = CreateAgent(llmClient);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));

        // Act
        var responses = await agent.RunStreamingAsync(
                "Search for the word 'unicorn' in both /library/ and /notes/ directories " +
                "using the domain:filesystem:text_search tool. Tell me which filesystem contains the word.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();
        var combined = string.Join(" ", responses.Select(r => r.Content)).ToLowerInvariant();
        combined.ShouldContain("library");

        await agent.DisposeAsync();
    }
}
