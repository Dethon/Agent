using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public sealed class CreateConversationTool
{
    [McpServerTool(Name = ChannelProtocol.CreateConversationTool)]
    [Description("Create a conversation that speaks a scheduled reply on voice satellites")]
    public static async Task<string> McpRun(
        [Description("Agent identifier")] string agentId,
        [Description("Topic display name")] string topicName,
        [Description("User who initiated")] string sender,
        IServiceProvider services,
        [Description("Text of the originating prompt")] string? initialPrompt = null,
        [Description("Satellite selector: a satellite id, or 'all'/empty for every satellite")] string? address = null)
    {
        var registry = services.GetRequiredService<SatelliteRegistry>();
        var delivery = services.GetRequiredService<VoiceDeliveryRegistry>();
        var factory = services.GetRequiredService<IConversationFactory>();

        var target = ParseTarget(address);
        if (target.SatelliteId is not null && registry.GetById(target.SatelliteId) is null)
        {
            throw new McpException($"Unknown voice satellite '{target.SatelliteId}'");
        }

        var creation = await factory.CreateAsync(new CreateConversationParams
        {
            AgentId = agentId,
            TopicName = topicName,
            Sender = sender,
            InitialPrompt = initialPrompt
        });

        delivery.Bind(creation.Identity.ConversationId, target);
        return creation.Identity.ConversationId;
    }

    private static AnnounceTarget ParseTarget(string? address) =>
        string.IsNullOrWhiteSpace(address) || address.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? new AnnounceTarget { All = true }
            : new AnnounceTarget { SatelliteId = address };
}