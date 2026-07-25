using System.Text.Json.Nodes;
using Shouldly;

namespace Tests.Unit.Agent;

// Pins which agents mount which MCP servers. An agent's mcpServerEndpoints list is the only
// per-agent input that decides what it can see, so a mount that is meant to be shared across
// agents is only shared if every one of them lists it.
public class AgentAppSettingsTests
{
    private const string TimersEndpoint = "http://mcp-timers:8080/mcp";
    private const string VoiceEndpoint = "http://mcp-channel-voice:8080/mcp";

    // mcp-timers hosts filesystem://timers as a pure tool server. Countdown timers are household
    // state, not per-agent state, so an agent reached from any channel must be able to read, arm and
    // dismiss them -- not only the voice agent that happened to create one.
    [Fact]
    public void McpServerEndpoints_Jonas_MountsTimersServer()
    {
        Endpoints("jonas").ShouldContain(TimersEndpoint);
    }

    [Fact]
    public void McpServerEndpoints_Nabu_MountsTimersServer()
    {
        Endpoints("nabu").ShouldContain(TimersEndpoint);
    }

    // Jack is the download bot: it has neither filesystem.create nor filesystem.exec, so it could
    // not arm or dismiss a timer anyway.
    [Fact]
    public void McpServerEndpoints_Jack_DoesNotMountTimersServer()
    {
        Endpoints("jack").ShouldNotContain(TimersEndpoint);
    }

    // The voice hub is a pure channel again: timers moved out to mcp-timers, so no agent mounts the
    // voice server as a tool server. It reaches agents only via channelEndpoints.
    [Fact]
    public void McpServerEndpoints_NoAgentMountsTheVoiceChannelServer()
    {
        foreach (var agentId in new[] { "jack", "jonas", "nabu" })
        {
            Endpoints(agentId).ShouldNotContain(VoiceEndpoint);
        }
    }

    private static string[] Endpoints(string agentId)
    {
        // Read the working tree, never AppContext.BaseDirectory: many referenced projects copy
        // their own appsettings.json to the test output, so Tests/bin/.../appsettings.json is
        // whichever one won the copy race -- not the Agent's. File.ReadAllText also strips the
        // UTF-8 BOM this file carries, which would otherwise fail JSON parsing.
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "Agent", "appsettings.json"));
        var agent = JsonNode.Parse(json)!["agents"]!.AsArray()
            .Single(a => a!["id"]!.GetValue<string>() == agentId)!;
        return [.. agent["mcpServerEndpoints"]!.AsArray().Select(e => e!.GetValue<string>())];
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "agent.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("agent.sln not found above test directory");
    }
}