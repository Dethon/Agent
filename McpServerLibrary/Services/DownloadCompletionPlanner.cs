using Domain.DTOs;
using Domain.DTOs.Channel;

namespace McpServerLibrary.Services;

public static class DownloadCompletionPlanner
{
    public static ChannelMessageNotification BuildPayload(DownloadRouting routing, DownloadItem item) => new()
    {
        ConversationId = routing.Context.ConversationId,
        Sender = routing.Context.UserId,
        Content = BuildPrompt(routing, item),
        AgentId = routing.Context.AgentId,
        ReplyTo = [routing.Context.Origin],
        Origin = new MessageOrigin(MessageOriginKind.Download, null),
        Timestamp = DateTimeOffset.UtcNow
    };

    private static string BuildPrompt(DownloadRouting routing, DownloadItem item) =>
        $"""
         [download-complete] Download '{routing.Title}' (id {routing.DownloadId}) has finished downloading to {item.SavePath}.
         Inform the user their download is ready and carry out any follow-up steps you promised for it (e.g. organizing it into the library).
         """;
}