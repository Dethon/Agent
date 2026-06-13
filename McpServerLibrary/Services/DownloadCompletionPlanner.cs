using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Tools.Downloads.Vfs;

namespace McpServerLibrary.Services;

public static class DownloadCompletionPlanner
{
    public static ChannelMessageNotification BuildPayload(DownloadRouting routing) => new()
    {
        ConversationId = routing.Context.ConversationId,
        // A completion alert is a system-originated event, not the user speaking. Attribute it
        // to the system sender so it never lands on the initiating user's identity (memory
        // scoping, message attribution); delivery still routes via ConversationId + ReplyTo.
        Sender = ChannelProtocol.SystemSender,
        Content = BuildPrompt(routing),
        AgentId = routing.Context.AgentId,
        ReplyTo = [routing.Context.Origin],
        Origin = new MessageOrigin(MessageOriginKind.Download, null),
        Timestamp = DateTimeOffset.UtcNow
    };

    private static string BuildPrompt(DownloadRouting routing) =>
        $"""
         [download-complete] Download '{routing.Title}' (id {routing.DownloadId}) has finished downloading to {MediaFilesystem.AgentDownloadDir(routing.DownloadId)}.
         Inform the user their download is ready and carry out any follow-up steps you promised for it (e.g. organizing it into the library).
         """;
}