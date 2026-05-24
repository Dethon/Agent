using Infrastructure.Agents;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

public class ThreadSessionToolFilterTests
{
    private static AITool Tool(string name) => AIFunctionFactory.Create(() => 0, name);

    [Fact]
    public void FilterMcpTools_ChannelProtocolTools_AreAlwaysRemoved()
    {
        AITool[] tools =
        [
            Tool("mcp__mcp-scheduling__send_reply"),
            Tool("mcp__mcp-scheduling__request_approval"),
            Tool("mcp__mcp-scheduling__register_agents"),
            Tool("mcp__mcp-scheduling__fs_glob")
        ];

        var result = ThreadSessionBuilder.FilterMcpTools(tools, filesystemToolsActive: false);

        result.Select(t => t.Name).ShouldBe(["mcp__mcp-scheduling__fs_glob"]);
    }

    [Fact]
    public void FilterMcpTools_FilesystemActive_RemovesRawFsTools()
    {
        AITool[] tools =
        [
            Tool("mcp__mcp-vault__fs_read"),
            Tool("mcp__mcp-vault__fs_exec"),
            Tool("mcp__mcp-websearch__web_browse")
        ];

        var result = ThreadSessionBuilder.FilterMcpTools(tools, filesystemToolsActive: true);

        result.Select(t => t.Name).ShouldBe(["mcp__mcp-websearch__web_browse"]);
    }

    [Fact]
    public void FilterMcpTools_FilesystemInactive_KeepsRawFsTools()
    {
        AITool[] tools =
        [
            Tool("mcp__mcp-vault__fs_read"),
            Tool("mcp__mcp-websearch__web_browse")
        ];

        var result = ThreadSessionBuilder.FilterMcpTools(tools, filesystemToolsActive: false);

        result.Select(t => t.Name).ShouldBe(["mcp__mcp-vault__fs_read", "mcp__mcp-websearch__web_browse"]);
    }

    [Fact]
    public void FilterMcpTools_NonReservedTools_ArePreserved()
    {
        AITool[] tools =
        [
            Tool("mcp__mcp-idealista__search_listings"),
            Tool("mcp__mcp-websearch__web_browse")
        ];

        var result = ThreadSessionBuilder.FilterMcpTools(tools, filesystemToolsActive: true);

        result.Select(t => t.Name)
            .ShouldBe(["mcp__mcp-idealista__search_listings", "mcp__mcp-websearch__web_browse"]);
    }
}