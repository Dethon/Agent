using Domain.Contracts;
using Domain.DTOs.WebChat;

namespace McpChannelSignalR.Services;

public sealed class SubAgentSignalService(
    SessionService sessionService,
    IHubNotificationSender hubNotificationSender,
    ILogger<SubAgentSignalService> logger) : ISubAgentSignalService
{
    public async Task AnnounceAsync(string conversationId, string handle, string subAgentId)
    {
        var topicId = sessionService.GetTopicIdByConversationId(conversationId) ?? conversationId;
        sessionService.TryGetSession(topicId, out var session);

        var notification = new SubAgentAnnouncedNotification(
            topicId, handle, subAgentId, SpaceSlug: session?.SpaceSlug);

        try
        {
            await SendToSpaceOrAllAsync(session?.SpaceSlug, "OnSubAgentAnnounced", notification);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to announce subagent {Handle} for topic {TopicId}", handle, topicId);
        }
    }

    public async Task UpdateAsync(string conversationId, string handle, string status)
    {
        var topicId = sessionService.GetTopicIdByConversationId(conversationId) ?? conversationId;
        sessionService.TryGetSession(topicId, out var session);

        var notification = new SubAgentUpdatedNotification(
            topicId, handle, status, SpaceSlug: session?.SpaceSlug);

        try
        {
            await SendToSpaceOrAllAsync(session?.SpaceSlug, "OnSubAgentUpdated", notification);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update subagent {Handle} status for topic {TopicId}", handle, topicId);
        }
    }

    private async Task SendToSpaceOrAllAsync(string? spaceSlug, string methodName, object notification)
    {
        if (spaceSlug is not null)
        {
            await hubNotificationSender.SendToGroupAsync($"space:{spaceSlug}", methodName, notification);
        }
        else
        {
            await hubNotificationSender.SendAsync(methodName, notification);
        }
    }
}
