using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.StateManagers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

public class McpAgentReasoningTests(RedisFixture redisFixture) : IClassFixture<RedisFixture>
{
    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddUserSecrets<McpAgentReasoningTests>()
        .Build();

    private static (string apiUrl, string apiKey, string model) GetConfig()
    {
        var apiKey = _configuration["openRouter:apiKey"]
                     ?? throw new SkipException("openRouter:apiKey not set in user secrets");
        var apiUrl = _configuration["openRouter:apiUrl"] ?? "https://openrouter.ai/api/v1/";
        var model = _configuration["openRouter:reasoningModel"] ?? "z-ai/glm-4.7-flash";
        return (apiUrl, apiKey, model);
    }

    [SkippableFact]
    public async Task Agent_WithReasoningEffortConfigured_StreamsReasoningContent()
    {
        // Drives a real OpenRouter call through McpAgent with reasoningEffort = "low"
        // and asserts that the model returns reasoning content — proves the per-agent
        // reasoning configuration actually reaches the wire and is honored end-to-end.
        var (apiUrl, apiKey, model) = GetConfig();

        using var openRouter = new OpenRouterChatClient(apiUrl, apiKey, model);
        var stateStore = new RedisThreadStateStore(redisFixture.Connection, TimeSpan.FromMinutes(10));

        await using var agent = new McpAgent(
            endpoints: [],
            chatClient: openRouter,
            name: "reasoning-agent",
            description: "",
            stateStore: stateStore,
            userId: "reasoning-test-user",
            reasoningEffort: "low");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var reasoningChunks = new List<string>();
        await foreach (var update in agent.RunStreamingAsync(
            "Compare 9.11 and 9.9. Which is larger? Show your reasoning.",
            cancellationToken: cts.Token))
        {
            foreach (var content in update.Contents.OfType<TextReasoningContent>())
            {
                reasoningChunks.Add(content.Text);
            }
        }

        var reasoning = string.Concat(reasoningChunks);
        reasoning.ShouldNotBeNullOrWhiteSpace(
            "McpAgent should propagate reasoningEffort='low' to OpenRouter so the provider streams reasoning tokens back.");
    }

    [SkippableFact]
    public async Task Agent_WithReasoningEffortNone_StreamsNoReasoningContent()
    {
        // With reasoningEffort = "none", the provider should NOT stream reasoning tokens —
        // proves that effort=none disables reasoning end-to-end (not just any non-null value
        // forces it on).
        var (apiUrl, apiKey, model) = GetConfig();

        using var openRouter = new OpenRouterChatClient(apiUrl, apiKey, model);
        var stateStore = new RedisThreadStateStore(redisFixture.Connection, TimeSpan.FromMinutes(10));

        await using var agent = new McpAgent(
            endpoints: [],
            chatClient: openRouter,
            name: "no-effort-agent",
            description: "",
            stateStore: stateStore,
            userId: "no-effort-test-user",
            reasoningEffort: "none");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var reasoningChunks = new List<string>();
        await foreach (var update in agent.RunStreamingAsync(
            "Compare 9.11 and 9.9. Which is larger? Show your reasoning.",
            cancellationToken: cts.Token))
        {
            foreach (var content in update.Contents.OfType<TextReasoningContent>())
            {
                reasoningChunks.Add(content.Text);
            }
        }

        var reasoning = string.Concat(reasoningChunks);
        reasoning.ShouldBeNullOrWhiteSpace(
            "McpAgent with reasoningEffort='none' should suppress reasoning tokens.");
    }

    [SkippableFact]
    public async Task Agent_WithoutReasoningEffort_DoesNotForceReasoning()
    {
        // Sanity check: when reasoningEffort is not configured, McpAgent should not be
        // injecting any Reasoning option; the call should still succeed against OpenRouter.
        var (apiUrl, apiKey, model) = GetConfig();

        using var openRouter = new OpenRouterChatClient(apiUrl, apiKey, model);
        var stateStore = new RedisThreadStateStore(redisFixture.Connection, TimeSpan.FromMinutes(10));

        await using var agent = new McpAgent(
            endpoints: [],
            chatClient: openRouter,
            name: "no-reasoning-agent",
            description: "",
            stateStore: stateStore,
            userId: "no-reasoning-test-user");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var receivedAny = false;
        await foreach (var _ in agent.RunStreamingAsync(
            "Reply with just the word OK.",
            cancellationToken: cts.Token))
        {
            receivedAny = true;
        }

        receivedAny.ShouldBeTrue("Agent should still succeed end-to-end without reasoning configured.");
    }
}
