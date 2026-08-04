using System.ComponentModel;
using Domain.DTOs.Channel;
using Infrastructure.Clients.Channels;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Shouldly;
using Tests.Integration.McpServers;

namespace Tests.Integration.Channels;

// The capability probe asks "does this server offer this tool". Nothing can change that answer
// while a connection is up — every server in this repo registers its tools before its transport
// starts — so it is asked once per connection generation. Driven against a real server over
// loopback, because the count is only meaningful if the round trips are real.
// See docs/adr/0012-a-servers-tool-set-is-fixed-for-a-connection-generation.md.
public class McpChannelConnectionToolCacheTests
{
    private static int _listToolsCalls;

    [Fact]
    public async Task CreateConversation_TwiceOnOneGeneration_AsksForTheToolSetOnce()
    {
        Interlocked.Exchange(ref _listToolsCalls, 0);
        await using var server = await StartServerAsync();
        await using var connection = new McpChannelConnection("test");
        await connection.ConnectAsync(server.Endpoint, CancellationToken.None);

        var before = Volatile.Read(ref _listToolsCalls);

        (await CreateAsync(connection)).ShouldBe("conv-1");
        (await CreateAsync(connection)).ShouldBe("conv-1");

        (Volatile.Read(ref _listToolsCalls) - before).ShouldBe(1);
    }

    [Fact]
    public async Task CreateConversation_AfterAReconnect_AsksAgain()
    {
        // An operator redeploying a channel server with a new tool must see it once the agent
        // reconnects: a cached answer may never outlive the process that gave it.
        Interlocked.Exchange(ref _listToolsCalls, 0);
        await using var server = await StartServerAsync();
        await using var connection = new McpChannelConnection("test");
        await connection.ConnectAsync(server.Endpoint, CancellationToken.None);

        var before = Volatile.Read(ref _listToolsCalls);
        await CreateAsync(connection);

        await connection.ReconnectAsync(server.Endpoint, CancellationToken.None);
        await CreateAsync(connection);

        (Volatile.Read(ref _listToolsCalls) - before).ShouldBe(2);
    }

    [Fact]
    public async Task CreateConversation_ServerWithoutTheTool_StillYieldsNull()
    {
        await using var server = await InMemoryMcpServer.StartAsync(services => services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<NoConversationTools>());
        await using var connection = new McpChannelConnection("test");
        await connection.ConnectAsync(server.Endpoint, CancellationToken.None);

        (await CreateAsync(connection)).ShouldBeNull();
    }

    private static Task<string?> CreateAsync(McpChannelConnection connection) =>
        connection.CreateConversationAsync(
            "agent-1", "topic", "sender", initialPrompt: null, address: null,
            existingConversationId: null, CancellationToken.None);

    private static Task<RunningServer> StartServerAsync() => InMemoryMcpServer.StartAsync(services => services
        .AddMcpServer()
        .WithHttpTransport()
        .WithTools<ConversationTools>()
        .WithRequestFilters(filters => filters.AddListToolsFilter(next => (context, ct) =>
        {
            Interlocked.Increment(ref _listToolsCalls);
            return next(context, ct);
        })));

    [McpServerToolType]
    public sealed class ConversationTools
    {
        [McpServerTool(Name = ChannelProtocol.CreateConversationTool)]
        [Description("Mint a conversation.")]
        public static string Create(
            string agentId, string topicName, string sender,
            string? initialPrompt, string? address, string? existingConversationId) => "conv-1";
    }

    [McpServerToolType]
    public sealed class NoConversationTools
    {
        [McpServerTool(Name = "unrelated")]
        [Description("A server that mints nothing.")]
        public static string Unrelated() => "ok";
    }
}