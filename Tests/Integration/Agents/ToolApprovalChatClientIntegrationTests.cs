using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Infrastructure.Agents;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

public class ToolApprovalChatClientIntegrationTests(McpLibraryServerFixture mcpFixture)
    : IClassFixture<McpLibraryServerFixture>
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

    [Fact]
    public async Task Agent_WithApprovalRequired_BlocksToolCallWhenRejected()
    {
        // Arrange
        var innerClient = CreateLlmClient();
        var rejectingHandler = new TestApprovalHandler(approved: false);
        var approvalClient = new ToolApprovalChatClient(innerClient, rejectingHandler);

        var agent = new McpAgent(
            [mcpFixture.McpEndpoint],
            approvalClient,
            "",
            "");

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

        // Assert
        responses.ShouldNotBeEmpty();

        // Verify the tool call was processed (rejection was requested)
        rejectingHandler.RequestedApprovals.ShouldNotBeEmpty();
        rejectingHandler.RequestedApprovals[0][0].ToolName.ShouldBe("ListDirectories");

        await agent.DisposeAsync();
    }

    [Fact]
    public async Task Agent_WithApprovalRequired_AllowsToolCallWhenApproved()
    {
        // Arrange
        var innerClient = CreateLlmClient();
        var approvingHandler = new TestApprovalHandler(approved: true);
        var approvalClient = new ToolApprovalChatClient(innerClient, approvingHandler);

        var agent = new McpAgent(
            [mcpFixture.McpEndpoint],
            approvalClient,
            "",
            "");

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
        var rejectingHandler = new TestApprovalHandler(approved: false);
        var approvalClient = new ToolApprovalChatClient(
            innerClient,
            rejectingHandler,
            whitelistedTools: ["ListDirectories"]);

        var agent = new McpAgent(
            [mcpFixture.McpEndpoint],
            approvalClient,
            "",
            "");

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
        var approvingHandler = new TestApprovalHandler(approved: true);
        var approvalClient = new ToolApprovalChatClient(
            innerClient,
            approvingHandler,
            whitelistedTools: ["ListDirectories"]);

        var agent = new McpAgent(
            [mcpFixture.McpEndpoint],
            approvalClient,
            "",
            "");

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

    private sealed class TestApprovalHandler(bool approved) : IToolApprovalHandler
    {
        public List<IReadOnlyList<ToolApprovalRequest>> RequestedApprovals { get; } = [];

        public Task<bool> RequestApprovalAsync(
            IReadOnlyList<ToolApprovalRequest> requests,
            CancellationToken cancellationToken)
        {
            RequestedApprovals.Add(requests);
            return Task.FromResult(approved);
        }
    }
}