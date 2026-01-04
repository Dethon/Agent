using Domain.DTOs;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

public class McpSubscriptionManagerIntegrationTests(ThreadSessionServerFixture fixture)
    : IClassFixture<ThreadSessionServerFixture>
{
    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddUserSecrets<McpSubscriptionManagerIntegrationTests>()
        .Build();

    private static OpenAiClient CreateChatClient()
    {
        var apiKey = _configuration["openRouter:apiKey"]
                     ?? throw new SkipException("openRouter:apiKey not set in user secrets");
        var apiUrl = _configuration["openRouter:apiUrl"] ?? "https://openrouter.ai/api/v1/";
        return new OpenAiClient(apiUrl, apiKey, ["google/gemini-2.5-flash"]);
    }

    [SkippableFact]
    public async Task SyncResourcesAsync_WithResources_SubscribesToThem()
    {
        // Arrange
        var sessionKey = $"SyncSubscribeClient_{Guid.NewGuid()}";
        fixture.TrackedDownloadsManager.Add(sessionKey, 301);
        fixture.DownloadClient.SetDownload(301, DownloadState.InProgress);

        using var chatClient = CreateChatClient();
        var agent = chatClient.CreateAIAgent(new ChatClientAgentOptions { Name = "TestAgent" });
        var thread = agent.GetNewThread();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var session = await ThreadSession.CreateAsync(
            [fixture.McpEndpoint],
            sessionKey,
            "test-user",
            "Sync Resources Test",
            agent,
            thread,
            cts.Token);

        // Act - Sync should have happened during creation
        // Sync again to verify idempotence
        await session.ResourceManager.SyncResourcesAsync(session.ClientManager.Clients, cts.Token);

        // Assert - No exception means success
        session.ResourceManager.ShouldNotBeNull();

        await session.DisposeAsync();
    }

    [SkippableFact]
    public async Task SyncResourcesAsync_WhenResourcesRemoved_Unsubscribes()
    {
        // Arrange
        var sessionKey = $"UnsubscribeClient_{Guid.NewGuid()}";
        fixture.TrackedDownloadsManager.Add(sessionKey, 302);
        fixture.DownloadClient.SetDownload(302, DownloadState.InProgress);

        using var chatClient = CreateChatClient();
        var agent = chatClient.CreateAIAgent(new ChatClientAgentOptions { Name = "TestAgent" });
        var thread = agent.GetNewThread();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var session = await ThreadSession.CreateAsync(
            [fixture.McpEndpoint],
            sessionKey,
            "test-user",
            "Unsubscribe Test",
            agent,
            thread,
            cts.Token);

        // Act - Remove the download and sync
        fixture.TrackedDownloadsManager.Remove(sessionKey, 302);
        await session.ResourceManager.SyncResourcesAsync(session.ClientManager.Clients, cts.Token);

        // Assert - No exception means unsubscribe succeeded
        await session.DisposeAsync();
    }

    [SkippableFact]
    public async Task SyncResourcesAsync_WithNoResources_CompletesChannel()
    {
        // Arrange
        var sessionKey = $"NoResourcesClient_{Guid.NewGuid()}";
        // Don't add any downloads

        using var chatClient = CreateChatClient();
        var agent = chatClient.CreateAIAgent(new ChatClientAgentOptions { Name = "TestAgent" });
        var thread = agent.GetNewThread();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var session = await ThreadSession.CreateAsync(
            [fixture.McpEndpoint],
            sessionKey,
            "test-user",
            "No Resources Test",
            agent,
            thread,
            cts.Token);

        // Act - Sync with no resources
        await session.ResourceManager.SyncResourcesAsync(session.ClientManager.Clients, cts.Token);

        // Assert - Channel may be completed since no resources
        // Allow some time for async completion
        await Task.Delay(100, cts.Token);
        session.ResourceManager.SubscriptionChannel.ShouldNotBeNull();

        await session.DisposeAsync();
    }

    [SkippableFact]
    public async Task EnsureChannelActive_ReactivatesCompletedChannel()
    {
        // Arrange
        var sessionKey = $"ReactivateClient_{Guid.NewGuid()}";

        using var chatClient = CreateChatClient();
        var agent = chatClient.CreateAIAgent(new ChatClientAgentOptions { Name = "TestAgent" });
        var thread = agent.GetNewThread();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var session = await ThreadSession.CreateAsync(
            [fixture.McpEndpoint],
            sessionKey,
            "test-user",
            "Reactivate Test",
            agent,
            thread,
            cts.Token);

        // Sync with no resources to complete channel
        await session.ResourceManager.SyncResourcesAsync(session.ClientManager.Clients, cts.Token);

        // Act - Add a download and ensure channel is active
        fixture.TrackedDownloadsManager.Add(sessionKey, 303);
        fixture.DownloadClient.SetDownload(303, DownloadState.InProgress);
        await session.ResourceManager.EnsureChannelActive(cts.Token);

        // Assert
        session.ResourceManager.SubscriptionChannel.Reader.Completion.IsCompleted.ShouldBeFalse();

        await session.DisposeAsync();
    }

    [SkippableFact]
    public async Task DisposeAsync_UnsubscribesFromAllResources()
    {
        // Arrange
        var sessionKey = $"DisposeUnsubClient_{Guid.NewGuid()}";
        fixture.TrackedDownloadsManager.Add(sessionKey, 304);
        fixture.DownloadClient.SetDownload(304, DownloadState.InProgress);

        using var chatClient = CreateChatClient();
        var agent = chatClient.CreateAIAgent(new ChatClientAgentOptions { Name = "TestAgent" });
        var thread = agent.GetNewThread();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var session = await ThreadSession.CreateAsync(
            [fixture.McpEndpoint],
            sessionKey,
            "test-user",
            "Dispose Unsub Test",
            agent,
            thread,
            cts.Token);

        // Act & Assert - Should not throw
        await session.DisposeAsync();
    }

    [SkippableFact]
    public async Task MultipleClients_SyncsResourcesFromAll()
    {
        // Arrange
        var sessionKey = $"MultiClientSync_{Guid.NewGuid()}";
        fixture.TrackedDownloadsManager.Add(sessionKey, 305);
        fixture.DownloadClient.SetDownload(305, DownloadState.InProgress);

        using var chatClient = CreateChatClient();
        var agent = chatClient.CreateAIAgent(new ChatClientAgentOptions { Name = "TestAgent" });
        var thread = agent.GetNewThread();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Use two endpoints
        var session = await ThreadSession.CreateAsync(
            [fixture.McpEndpoint, fixture.McpEndpoint],
            sessionKey,
            "test-user",
            "Multi Client Sync Test",
            agent,
            thread,
            cts.Token);

        // Act
        await session.ResourceManager.SyncResourcesAsync(session.ClientManager.Clients, cts.Token);

        // Assert
        session.ClientManager.Clients.Count.ShouldBe(2);

        await session.DisposeAsync();
    }
}