using Domain.Extensions;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.StateManagers;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

public class McpAgentFileSystemTests(McpVaultServerFixture vaultFixture, RedisFixture redisFixture)
    : IClassFixture<McpVaultServerFixture>, IClassFixture<RedisFixture>
{
    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddUserSecrets<McpAgentFileSystemTests>()
        .Build();

    private static readonly HashSet<string> _allFileSystemTools =
        ["read", "create", "edit", "glob", "search", "move", "remove"];

    private static OpenRouterChatClient CreateLlmClient()
    {
        var apiKey = _configuration["openRouter:apiKey"]
                     ?? throw new SkipException("openRouter:apiKey not set in user secrets");
        var apiUrl = _configuration["openRouter:apiUrl"] ?? "https://openrouter.ai/api/v1/";

        return new OpenRouterChatClient(apiUrl, apiKey, "z-ai/glm-4.7-flash");
    }

    private McpAgent CreateAgent(OpenRouterChatClient llmClient, IReadOnlySet<string>? enabledTools = null)
    {
        var stateStore = new RedisThreadStateStore(redisFixture.Connection, TimeSpan.FromMinutes(10));
        return new McpAgent(
            [vaultFixture.McpEndpoint],
            llmClient,
            "test-fs-agent",
            "",
            stateStore,
            "test-user",
            filesystemEnabledTools: enabledTools ?? _allFileSystemTools);
    }

    [SkippableFact]
    public async Task Agent_WithFileSystemFeature_CanReadFile()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        vaultFixture.CreateFile("read-test.md", "# Secret Document\nThis is the content.");

        var agent = CreateAgent(llmClient);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var responses = await agent.RunStreamingAsync(
                "Read the file at /library/read-test.md using the domain:filesystem:text_read tool and tell me its content.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();
        var combinedResponse = string.Join(" ", responses.Select(r => r.Content));
        combinedResponse.ShouldContain("Secret Document");

        await agent.DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_WithFileSystemFeature_CanCreateFile()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        var agent = CreateAgent(llmClient);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var responses = await agent.RunStreamingAsync(
                "Create a file at /library/created-by-agent.md with content '# Created\nHello from agent' using the domain:filesystem:text_create tool.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();
        var filePath = Path.Combine(vaultFixture.VaultPath, "created-by-agent.md");
        File.Exists(filePath).ShouldBeTrue("Agent should have created the file");
        var content = await File.ReadAllTextAsync(filePath);
        content.ShouldContain("Created");

        await agent.DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_WithFileSystemFeature_CanEditFile()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        vaultFixture.CreateFile("edit-test.md", "Hello World");

        var agent = CreateAgent(llmClient);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var responses = await agent.RunStreamingAsync(
                "Edit the file at /library/edit-test.md: replace 'World' with 'Agent' using the domain:filesystem:text_edit tool.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();
        var content = await File.ReadAllTextAsync(Path.Combine(vaultFixture.VaultPath, "edit-test.md"));
        content.ShouldContain("Agent");
        content.ShouldNotContain("World");

        await agent.DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_WithFileSystemFeature_CanGlobFiles()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        vaultFixture.CreateFile(Path.Combine("glob-test", "notes.md"), "note");
        vaultFixture.CreateFile(Path.Combine("glob-test", "readme.md"), "readme");
        vaultFixture.CreateFile(Path.Combine("glob-test", "data.json"), "{}");

        var agent = CreateAgent(llmClient);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var responses = await agent.RunStreamingAsync(
                "List all .md files under /library/glob-test/ using the domain:filesystem:glob_files tool with pattern **/*.md.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();
        var hasContent = responses.Any(r => !string.IsNullOrEmpty(r.Content) || !string.IsNullOrEmpty(r.ToolCalls));
        hasContent.ShouldBeTrue("Agent should have produced content or tool calls for glob");

        // Verify the agent found .md files (response is non-deterministic, so check broadly)
        var combined = string.Join(" ", responses.Select(r => r.Content + " " + r.ToolCalls)).ToLowerInvariant();
        (combined.Contains("notes") || combined.Contains("readme") || combined.Contains(".md"))
            .ShouldBeTrue("Agent response should reference the found .md files");

        await agent.DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_WithFileSystemFeature_CanSearchFiles()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        vaultFixture.CreateFile(Path.Combine("search-test", "doc1.md"), "The quick brown fox jumps over the lazy dog.");
        vaultFixture.CreateFile(Path.Combine("search-test", "doc2.md"), "A different document without the target phrase.");

        var agent = CreateAgent(llmClient);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var responses = await agent.RunStreamingAsync(
                "Search for the text 'quick brown fox' in /library/search-test/ using the domain:filesystem:text_search tool.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();
        var combinedResponse = string.Join(" ", responses.Select(r => r.Content + " " + r.ToolCalls));
        combinedResponse.ShouldContain("doc1");

        await agent.DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_WithFileSystemFeature_CanMoveFile()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        vaultFixture.CreateFile(Path.Combine("move-src", "moveme.md"), "move content");
        Directory.CreateDirectory(Path.Combine(vaultFixture.VaultPath, "move-dst"));

        var agent = CreateAgent(llmClient);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var responses = await agent.RunStreamingAsync(
                "Move the file /library/move-src/moveme.md to /library/move-dst/moveme.md using the domain:filesystem:move tool.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();
        File.Exists(Path.Combine(vaultFixture.VaultPath, "move-dst", "moveme.md")).ShouldBeTrue();
        File.Exists(Path.Combine(vaultFixture.VaultPath, "move-src", "moveme.md")).ShouldBeFalse();

        await agent.DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_WithFileSystemFeature_CanRemoveFile()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        vaultFixture.CreateFile("remove-me.md", "to be deleted");

        var agent = CreateAgent(llmClient);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var responses = await agent.RunStreamingAsync(
                "Delete the file at /library/remove-me.md using the domain:filesystem:remove tool.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();
        File.Exists(Path.Combine(vaultFixture.VaultPath, "remove-me.md")).ShouldBeFalse();

        await agent.DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_WithFileSystemFeature_HasFileSystemPromptInInstructions()
    {
        // Arrange
        var llmClient = CreateLlmClient();
        vaultFixture.CreateFile("prompt-test.md", "test");

        var agent = CreateAgent(llmClient);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act - ask about available filesystems to verify prompt injection
        var responses = await agent.RunStreamingAsync(
                "What filesystems are available to you? List their mount points.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();
        var combinedResponse = string.Join(" ", responses.Select(r => r.Content)).ToLowerInvariant();
        (combinedResponse.Contains("/library") || combinedResponse.Contains("library"))
            .ShouldBeTrue("Agent should mention the library filesystem in its response");

        await agent.DisposeAsync();
    }

    [SkippableFact]
    public async Task Agent_WithSubsetOfFileSystemTools_CanStillUseEnabledTools()
    {
        // Arrange - only enable read and glob
        var llmClient = CreateLlmClient();
        vaultFixture.CreateFile(Path.Combine("subset-test", "info.md"), "subset content");

        var enabledTools = new HashSet<string> { "read", "glob" };
        var agent = CreateAgent(llmClient, enabledTools);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act - use glob (an enabled tool) to find files
        var responses = await agent.RunStreamingAsync(
                "Find all .md files under /library/subset-test/ using the domain:filesystem:glob_files tool with pattern **/*.md.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();
        var hasContent = responses.Any(r => !string.IsNullOrEmpty(r.Content) || !string.IsNullOrEmpty(r.ToolCalls));
        hasContent.ShouldBeTrue("Agent should have produced content or tool calls");

        await agent.DisposeAsync();
    }
}
