using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using McpChannelSignalR.Services;
using ModelContextProtocol.Server;

namespace McpChannelSignalR.McpTools;

[McpServerToolType]
public sealed class CreateConversationTool
{
    [McpServerTool(Name = ChannelProtocol.CreateConversationTool)]
    [Description("Create a new conversation for agent-initiated messages")]
    public static async Task<string> McpRun(
        [Description("Agent identifier")] string agentId,
        [Description("Topic display name")] string topicName,
        [Description("User who initiated")] string sender,
        IServiceProvider services,
        [Description("Text of the originating prompt; rendered as the user-role bubble")] string? initialPrompt = null,
        [Description("Unused on this channel; voice uses it for satellite targeting")] string? address = null,
        [Description("Unused on this channel; the WebChat channel always owns/mints the shared conversation")] string? existingConversationId = null)
    {
        var p = new CreateConversationParams
        {
            AgentId = agentId,
            TopicName = topicName,
            Sender = sender,
            InitialPrompt = initialPrompt
        };

        // Shared factory generates the conversation identity and persists the topic
        // to Redis (single source of truth shared with the voice channel).
        var factory = services.GetRequiredService<IConversationFactory>();
        var creation = await factory.CreateAsync(p);

        // Register the in-memory session so send_reply/request_approval can resolve it.
        var sessionService = services.GetRequiredService<SessionService>();
        sessionService.StartSession(
            creation.Identity.TopicId, agentId, creation.Identity.ChatId, creation.Identity.ThreadId,
            spaceSlug: "default", topicName: topicName);

        // Notify WebChat clients so the topic appears without refresh.
        var hubSender = services.GetRequiredService<IHubNotificationSender>();
        var notification = new TopicChangedNotification(
            TopicChangeType.Created, creation.Identity.TopicId, creation.Topic, SpaceSlug: "default");
        await hubSender.SendToGroupAsync("space:default", "OnTopicChanged", notification);

        // Create a stream so send_reply chunks have somewhere to go. The stream's
        // currentPrompt seeds the user-role bubble on WebChat, so it must be the
        // originating prompt — falling back to topicName for legacy callers.
        var streamService = services.GetRequiredService<StreamService>();
        streamService.GetOrCreateStream(creation.Identity.TopicId, initialPrompt ?? topicName, sender, CancellationToken.None);

        return creation.Identity.ConversationId;
    }
}