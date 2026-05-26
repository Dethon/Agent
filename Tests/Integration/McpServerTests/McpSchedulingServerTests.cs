using Domain.DTOs.Channel;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

public class McpSchedulingServerTests(McpSchedulingServerFixture fixture) : IClassFixture<McpSchedulingServerFixture>
{
    private async Task<McpClient> ConnectAsync() =>
        await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(fixture.McpEndpoint) }),
            cancellationToken: CancellationToken.None);

    [Fact]
    public async Task McpServer_ListResources_ReturnsSchedulesFilesystem()
    {
        var client = await ConnectAsync();

        var resources = await client.ListResourcesAsync();

        resources.ShouldContain(r => r.Uri == "filesystem://schedules");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task McpServer_ReadFilesystemResource_ReturnsMetadata()
    {
        var client = await ConnectAsync();

        var content = await client.ReadResourceAsync("filesystem://schedules");
        var text = string.Join("", content.Contents
            .OfType<TextResourceContents>()
            .Select(c => c.Text));

        text.ShouldContain("\"name\":\"schedules\"");
        text.ShouldContain("\"mountPoint\":\"/schedules\"");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task McpServer_ListPrompts_IncludesSchedulingPrompt()
    {
        var client = await ConnectAsync();

        var prompts = await client.ListPromptsAsync();

        prompts.ShouldContain(p => p.Name == "scheduling_prompt");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task McpServer_GetSchedulingPrompt_ExplainsCronAndScheduleFile()
    {
        var client = await ConnectAsync();

        var result = await client.GetPromptAsync("scheduling_prompt");
        var text = string.Join("", result.Messages
            .Select(m => m.Content)
            .OfType<TextContentBlock>()
            .Select(c => c.Text));

        text.ShouldContain("schedule.json");
        text.ShouldContain("0 9 * * *");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task McpServer_GetSchedulingPrompt_IncludesRegisteredAgents()
    {
        var client = await ConnectAsync();

        var register = await client.CallToolAsync(
            ChannelProtocol.RegisterAgentsTool,
            ChannelProtocol.ToArguments(new RegisterAgentsParams
            {
                Agents =
                [
                    new AgentCatalogEntry("itest-summary-agent", "Summary Agent", "Used by the snippet test."),
                ]
            }),
            cancellationToken: CancellationToken.None);
        (register.IsError ?? false).ShouldBeFalse();

        var result = await client.GetPromptAsync("scheduling_prompt");
        var text = string.Join("", result.Messages
            .Select(m => m.Content)
            .OfType<TextContentBlock>()
            .Select(c => c.Text));

        text.ShouldContain("## Current scheduling setup");
        text.ShouldContain("/schedules/itest-summary-agent");
        text.ShouldContain("- `itest-summary-agent` (Summary Agent) — Used by the snippet test.");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task McpServer_ListTools_IncludesFsTools()
    {
        var client = await ConnectAsync();

        var toolNames = (await client.ListToolsAsync()).Select(t => t.Name).ToList();

        toolNames.ShouldContain("fs_create");
        toolNames.ShouldContain("fs_glob");
        toolNames.ShouldContain("fs_read");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task McpServer_ListTools_IncludesRegisterAgents()
    {
        var client = await ConnectAsync();

        var toolNames = (await client.ListToolsAsync()).Select(t => t.Name).ToList();

        toolNames.ShouldContain("register_agents");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task RegisterAgents_ThenCreateForNewAgent_PassesValidation()
    {
        var client = await ConnectAsync();

        var register = await client.CallToolAsync(
            ChannelProtocol.RegisterAgentsTool,
            ChannelProtocol.ToArguments(new RegisterAgentsParams
            {
                Agents = [new AgentCatalogEntry("jonas", "Jonas", "general"), new AgentCatalogEntry("jack", "Jack", "downloads")]
            }),
            cancellationToken: CancellationToken.None);

        (register.IsError ?? false).ShouldBeFalse();

        var create = await client.CallToolAsync(
            "fs_create",
            new Dictionary<string, object?>
            {
                ["path"] = "/jack/itest-register/schedule.json",
                ["content"] = """{"prompt":"do the thing","cron":"0 7 * * *"}"""
            },
            cancellationToken: CancellationToken.None);

        (create.IsError ?? false).ShouldBeFalse();

        var info = await client.CallToolAsync(
            "fs_read",
            new Dictionary<string, object?> { ["path"] = "/jack/agent_info.json" },
            cancellationToken: CancellationToken.None);

        info.Content.OfType<TextContentBlock>().First().Text.ShouldContain("Jack");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task CreateGlobRead_RoundTrip_PersistsAndReadsSchedule()
    {
        var client = await ConnectAsync();

        var create = await client.CallToolAsync(
            "fs_create",
            new Dictionary<string, object?>
            {
                ["path"] = "/jonas/itest-news/schedule.json",
                ["content"] = """{"prompt":"summarize the integration news","cron":"0 8 * * *"}"""
            },
            cancellationToken: CancellationToken.None);

        (create.IsError ?? false).ShouldBeFalse();

        var glob = await client.CallToolAsync(
            "fs_glob",
            new Dictionary<string, object?> { ["pattern"] = "*", ["basePath"] = "/jonas" },
            cancellationToken: CancellationToken.None);

        glob.Content.OfType<TextContentBlock>().First().Text.ShouldContain("/jonas/itest-news");

        var read = await client.CallToolAsync(
            "fs_read",
            new Dictionary<string, object?> { ["path"] = "/jonas/itest-news/schedule.json" },
            cancellationToken: CancellationToken.None);

        read.Content.OfType<TextContentBlock>().First().Text.ShouldContain("summarize the integration news");

        await client.DisposeAsync();
    }
}