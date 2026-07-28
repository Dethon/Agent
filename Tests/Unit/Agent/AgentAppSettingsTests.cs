using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.Prompts;
using Infrastructure.Agents;
using Microsoft.Extensions.Configuration;
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

    // Nabu speaks through a Spanish TTS voice and reads a transcript from an STT pinned to
    // Spanish, while everything else in its request -- prompts, tool results, memory block --
    // is English. Declaring the language makes the reply language an absolute contract instead
    // of a relative rule the English context can outvote.
    [Fact]
    public void Language_Nabu_DeclaresSpanish()
    {
        Agent("nabu")["language"]!.GetValue<string>().ShouldBe("es");
    }

    // Two contracts for the same thing is how the drift got in: a relative rule sitting beside
    // the absolute one lets a short transcript surrounded by English resolve to English.
    [Fact]
    public void CustomInstructions_Nabu_CarryNoCompetingRelativeLanguageRule()
    {
        Agent("nabu")["customInstructions"]!.GetValue<string>()
            .ShouldNotContain("the language the user spoke");
    }

    // The rest of the chain: appsettings -> AgentDefinition -> system prompt. Every hop binds by
    // convention, so a renamed or dropped key fails nothing at build time -- the agent just
    // quietly goes back to inferring its language from an all-English request.
    [Fact]
    public void Language_Nabu_ReachesTheSystemPromptAsItsLastSection()
    {
        var nabu = BoundAgents().Single(a => a.Id == "nabu");
        nabu.Language.ShouldBe("es");

        McpAgent.BuildInstructions(
                name: nabu.Name,
                description: nabu.Description,
                customInstructions: nabu.CustomInstructions,
                language: nabu.Language,
                domainPrompts: [],
                fileSystemPrompts: [],
                clientPrompts: [],
                now: DateTimeOffset.UnixEpoch)
            .ShouldEndWith(LanguagePrompt.Build("es")!);
    }

    // Sort choices are deliberate per-agent decisions that nothing else would catch if reverted.
    // Nabu is the voice agent: time-to-first-token gates when speech starts, which is what
    // `latency` sorts on, where `throughput` sorts on sustained tokens/second -- the wrong metric
    // for replies capped at one short sentence.
    [Fact]
    public void ProviderRouting_Nabu_SortsByLatency()
    {
        Agent("nabu")["providerRouting"]!["sort"]!.GetValue<string>().ShouldBe("latency");
    }

    // `latency` alone ranks on time-to-first-token and says nothing about what happens after it,
    // so the fastest-answering provider can still be the one that dribbles the rest of the reply
    // out. The floor deprioritizes those without excluding anyone -- a threshold nobody meets
    // still routes -- which is why it can sit under a latency sort without risking a dead turn.
    [Fact]
    public void ProviderRouting_Nabu_FloorsThroughputUnderTheLatencySort()
    {
        Agent("nabu")["providerRouting"]!["preferredMinThroughput"]!.GetValue<double>().ShouldBe(80);
    }

    [Fact]
    public void ProviderRouting_JonasWorker_SortsByThroughput()
    {
        SubAgent("jonas-worker")["providerRouting"]!["sort"]!.GetValue<string>().ShouldBe("throughput");
    }

    // The raw-JSON pins above prove what the file says; these prove the binder delivers it.
    // ProviderRouting binds by naming convention with no ErrorOnUnknownConfiguration anywhere,
    // so a renamed property would leave the JSON key silently ignored -- every raw pin still
    // green while nabu quietly reverts to balanced routing. Same silent-severing hazard the
    // language test above exists for.
    [Fact]
    public void ProviderRouting_Nabu_ReachesAgentDefinition()
    {
        var nabu = BoundAgents().Single(a => a.Id == "nabu");

        nabu.ProviderRouting.ShouldNotBeNull();
        nabu.ProviderRouting!.Sort.ShouldBe(ProviderSort.Latency);
        nabu.ProviderRouting.PreferredMinThroughput!.P50.ShouldBe(80);
    }

    [Fact]
    public void ProviderRouting_JonasWorker_ReachesSubAgentDefinition()
    {
        var worker = BoundSubAgents().Single(a => a.Id == "jonas-worker");

        worker.ProviderRouting.ShouldNotBeNull();
        worker.ProviderRouting!.Sort.ShouldBe(ProviderSort.Throughput);
    }

    // Balanced routing is the absence of a provider object -- there is no `sort` value for it --
    // so it can only be asserted as an absence, never read back off a request.
    [Theory]
    [InlineData("jack")]
    [InlineData("jonas")]
    public void ProviderRouting_BalancedAgents_DeclareNone(string agentId)
    {
        Agent(agentId).AsObject().ContainsKey("providerRouting").ShouldBeFalse();
    }

    // One line added here would move every non-overriding caller -- Jack, Jonas and both memory
    // models -- off load balancing at once, silently.
    [Fact]
    public void ProviderRouting_GlobalDefault_IsUnset()
    {
        Root()["openRouter"]!.AsObject().ContainsKey("providerRouting").ShouldBeFalse();
    }

    // The migration exists to remove the dual-idiom problem; a pasted suffix would bring it back.
    [Fact]
    public void Model_NoAgentOrSubAgent_CarriesARoutingSuffix()
    {
        var models = Root()["agents"]!.AsArray()
            .Concat(Root()["subAgents"]!.AsArray())
            .Select(a => a!["model"]!.GetValue<string>());

        models.ShouldAllBe(m => !m.Contains(":nitro") && !m.Contains(":floor"));
    }

    private static AgentDefinition[] BoundAgents() =>
        BoundConfig().GetSection("agents").Get<AgentDefinition[]>()!;

    private static SubAgentDefinition[] BoundSubAgents() =>
        BoundConfig().GetSection("subAgents").Get<SubAgentDefinition[]>()!;

    private static IConfigurationRoot BoundConfig() =>
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepoRoot(), "Agent", "appsettings.json"))
            .Build();

    private static string[] Endpoints(string agentId) =>
        [.. Agent(agentId)["mcpServerEndpoints"]!.AsArray().Select(e => e!.GetValue<string>())];

    private static JsonNode Agent(string agentId) =>
        Root()["agents"]!.AsArray().Single(a => a!["id"]!.GetValue<string>() == agentId)!;

    private static JsonNode SubAgent(string subAgentId) =>
        Root()["subAgents"]!.AsArray().Single(a => a!["id"]!.GetValue<string>() == subAgentId)!;

    private static JsonNode Root()
    {
        // Read the working tree, never AppContext.BaseDirectory: many referenced projects copy
        // their own appsettings.json to the test output, so Tests/bin/.../appsettings.json is
        // whichever one won the copy race -- not the Agent's. File.ReadAllText also strips the
        // UTF-8 BOM this file carries, which would otherwise fail JSON parsing.
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "Agent", "appsettings.json"));
        return JsonNode.Parse(json)!;
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