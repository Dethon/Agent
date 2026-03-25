using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Domain.Tools.SubAgents;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.StateManagers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

public class SubAgentIntegrationTests(RedisFixture redisFixture)
    : IClassFixture<RedisFixture>
{
    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddUserSecrets<SubAgentIntegrationTests>()
        .Build();

    private static OpenRouterChatClient CreateLlmClient()
    {
        var apiKey = _configuration["openRouter:apiKey"]
                     ?? throw new SkipException("openRouter:apiKey not set in user secrets");
        var apiUrl = _configuration["openRouter:apiUrl"] ?? "https://openrouter.ai/api/v1/";
        return new OpenRouterChatClient(apiUrl, apiKey, "google/gemini-2.5-flash");
    }

    private static OpenRouterConfig CreateOpenRouterConfig()
    {
        var apiKey = _configuration["openRouter:apiKey"]
                     ?? throw new SkipException("openRouter:apiKey not set in user secrets");
        var apiUrl = _configuration["openRouter:apiUrl"] ?? "https://openrouter.ai/api/v1/";
        return new OpenRouterConfig { ApiUrl = apiUrl, ApiKey = apiKey };
    }

    [SkippableFact]
    public async Task SubAgent_CompletesTask_ReturnsResult()
    {
        var subAgentDef = new SubAgentDefinition
        {
            Id = "echo-agent",
            Name = "Echo",
            Description = "Echoes back what you say",
            Model = "google/gemini-2.5-flash",
            McpServerEndpoints = [],
            CustomInstructions = "You are a simple echo agent. Repeat back exactly what the user says, nothing more."
        };

        var openRouterConfig = CreateOpenRouterConfig();
        var domainToolRegistry = new DomainToolRegistry([]);
        var runner = new SubAgentRunner(openRouterConfig, domainToolRegistry);
        var contextAccessor = new SubAgentContextAccessor();
        var runTool = new SubAgentRunTool(
            runner, contextAccessor,
            new SubAgentRegistryOptions { SubAgents = [subAgentDef] });
        var toolFeature = new SubAgentToolFeature(runTool);

        var approvalHandler = new AutoApproveHandler();
        var agentName = "parent-agent-test";
        contextAccessor.SetContext(agentName, new SubAgentContext(approvalHandler, ["domain:subagents:*"], "test-user"));

        var llmClient = CreateLlmClient();
        var stateStore = new RedisThreadStateStore(redisFixture.Connection, TimeSpan.FromMinutes(5));
        using var effectiveClient = new ToolApprovalChatClient(llmClient, approvalHandler, ["domain:subagents:*"]);

        await using var agent = new McpAgent(
            [],
            effectiveClient,
            agentName,
            "",
            stateStore,
            "test-user",
            $"Your agent name is '{agentName}'. You have access to a subagent tool. Use the echo-agent subagent to echo back: 'Hello from subagent'",
            toolFeature.GetTools().ToList());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var responses = await agent.RunStreamingAsync(
                "Use the run_subagent tool with echo-agent to echo: 'Hello from subagent'. Pass your agent name as the agentName parameter.",
                cancellationToken: cts.Token)
            .ToUpdateAiResponsePairs()
            .Where(x => x.Item2 is not null)
            .Select(x => x.Item2!)
            .ToListAsync(cts.Token);

        responses.ShouldNotBeEmpty();
        var combined = string.Join(" ", responses.Select(r => r.Content).Where(c => !string.IsNullOrEmpty(c)));
        combined.ShouldContain("Hello from subagent", Case.Insensitive);
    }

    [SkippableFact]
    public async Task SubAgent_EphemeralState_NoRedisKeys()
    {
        var subAgentDef = new SubAgentDefinition
        {
            Id = "test-ephemeral",
            Name = "TestEphemeral",
            Model = "google/gemini-2.5-flash",
            McpServerEndpoints = [],
            CustomInstructions = "Reply with exactly the word 'done'."
        };

        var openRouterConfig = CreateOpenRouterConfig();
        var runner = new SubAgentRunner(openRouterConfig, new DomainToolRegistry([]));
        var approvalHandler = new AutoApproveHandler();
        var context = new SubAgentContext(approvalHandler, [], "test-user");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var server = redisFixture.Connection.GetServer(redisFixture.Connection.GetEndPoints()[0]);
        var keysBefore = server.Keys(pattern: "*").ToList();

        var result = await runner.RunAsync(subAgentDef, "Say done", context, cts.Token);

        var keysAfter = server.Keys(pattern: "*").ToList();
        keysAfter.Count.ShouldBe(keysBefore.Count,
            "SubAgent runner should use NullThreadStateStore and write no Redis keys");
        result.ShouldNotBeNullOrEmpty();
    }
}

file sealed class AutoApproveHandler : IToolApprovalHandler
{
    public Task<ToolApprovalResult> RequestApprovalAsync(
        IReadOnlyList<ToolApprovalRequest> requests, CancellationToken cancellationToken)
        => Task.FromResult(ToolApprovalResult.Approved);

    public Task NotifyAutoApprovedAsync(
        IReadOnlyList<ToolApprovalRequest> requests, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
