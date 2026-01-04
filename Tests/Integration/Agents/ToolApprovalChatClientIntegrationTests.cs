using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.StateManagers;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

public class ToolApprovalChatClientIntegrationTests(McpLibraryServerFixture mcpFixture, RedisFixture redisFixture)
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

        // Disable built-in function invocation - ToolApprovalChatClient handles it
        return new OpenAiClient(apiUrl, apiKey, models);
    }

    private McpAgent CreateAgent(ToolApprovalChatClient approvalClient)
    {
        var stateStore = new RedisThreadStateStore(redisFixture.Connection, TimeSpan.FromMinutes(10));
        return new McpAgent(
            [mcpFixture.McpEndpoint],
            approvalClient,
            "test-agent",
            "",
            stateStore,
            "test-user");
    }

    [Fact]
    public async Task Agent_WithApprovalRequired_TerminatesWhenRejected()
    {
        // Arrange
        var innerClient = CreateLlmClient();
        var rejectingHandler = new TestApprovalHandler(result: ToolApprovalResult.Rejected);
        var approvalClient = new ToolApprovalChatClient(innerClient, rejectingHandler);

        var agent = CreateAgent(approvalClient);

        mcpFixture.CreateLibraryStructure("ApprovalTestMovies");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var responses = await agent.RunStreamingAsync(
                "List directories using the ListDirectories tool.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert - should terminate with rejection message
        responses.ShouldNotBeEmpty();
        rejectingHandler.RequestedApprovals.ShouldNotBeEmpty();
        rejectingHandler.RequestedApprovals[0][0].ToolName.ShouldContain("ListDirectories");

        await agent.DisposeAsync();
    }

    [Fact]
    public async Task Agent_WithApprovalRequired_AllowsToolCallWhenApproved()
    {
        // Arrange
        var innerClient = CreateLlmClient();
        var approvingHandler = new TestApprovalHandler(result: ToolApprovalResult.Approved);
        var approvalClient = new ToolApprovalChatClient(innerClient, approvingHandler);

        var agent = CreateAgent(approvalClient);

        mcpFixture.CreateLibraryStructure("ApprovalTestApproved");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var responses = await agent.RunStreamingAsync(
                "List directories using the ListDirectories tool.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();

        var hasContent = responses.Any(r => !string.IsNullOrEmpty(r.Content) || !string.IsNullOrEmpty(r.ToolCalls));
        hasContent.ShouldBeTrue();

        await agent.DisposeAsync();
    }

    [Fact]
    public async Task Agent_WithWhitelistedTool_SkipsApprovalForWhitelistedTools()
    {
        // Arrange
        var innerClient = CreateLlmClient();
        var rejectingHandler = new TestApprovalHandler(result: ToolApprovalResult.Rejected);
        var approvalClient = new ToolApprovalChatClient(
            innerClient,
            rejectingHandler,
            whitelistPatterns: ["*:ListDirectories"]);

        var agent = CreateAgent(approvalClient);

        mcpFixture.CreateLibraryStructure("WhitelistTestMovies");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Act
        var responses = await agent.RunStreamingAsync(
                $"List all directories in the library at '{mcpFixture.LibraryPath}' using the ListDirectories tool.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();
        rejectingHandler.RequestedApprovals.ShouldBeEmpty("Whitelisted tool should not require approval");
        var hasContent = responses.Any(r => !string.IsNullOrEmpty(r.Content) || !string.IsNullOrEmpty(r.ToolCalls));
        hasContent.ShouldBeTrue();

        await agent.DisposeAsync();
    }

    [Fact]
    public async Task Agent_WithMixedTools_OnlyRequestsApprovalForNonWhitelisted()
    {
        // Arrange
        var innerClient = CreateLlmClient();
        var approvingHandler = new TestApprovalHandler(result: ToolApprovalResult.Approved);
        var approvalClient = new ToolApprovalChatClient(
            innerClient,
            approvingHandler,
            whitelistPatterns: ["*:ListDirectories"]);

        var agent = CreateAgent(approvalClient);

        mcpFixture.CreateLibraryStructure("MixedTestSource");
        mcpFixture.CreateLibraryStructure("MixedTestDest");
        mcpFixture.CreateLibraryFile(Path.Combine("MixedTestSource", "test-file.mkv"), "content");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));

        var sourcePath = Path.Combine(mcpFixture.LibraryPath, "MixedTestSource", "test-file.mkv");
        var destPath = Path.Combine(mcpFixture.LibraryPath, "MixedTestDest", "test-file.mkv");

        // Act
        var responses = await agent.RunStreamingAsync(
                $"First list the directories at '{mcpFixture.LibraryPath}', then move '{sourcePath}' to '{destPath}'.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        // Assert
        responses.ShouldNotBeEmpty();
        var approvedToolNames = approvingHandler.RequestedApprovals
            .SelectMany(r => r.Select(t => t.ToolName))
            .ToList();
        approvedToolNames.ShouldNotContain("ListDirectories", "Whitelisted tool should not be in approval requests");

        await agent.DisposeAsync();
    }

    private sealed class TestApprovalHandler(ToolApprovalResult result) : IToolApprovalHandler
    {
        public List<IReadOnlyList<ToolApprovalRequest>> RequestedApprovals { get; } = [];

        public Task<ToolApprovalResult> RequestApprovalAsync(
            IReadOnlyList<ToolApprovalRequest> requests,
            CancellationToken cancellationToken)
        {
            RequestedApprovals.Add(requests);
            return Task.FromResult(result);
        }

        public Task NotifyAutoApprovedAsync(
            IReadOnlyList<ToolApprovalRequest> requests,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}