using System.ComponentModel;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public sealed class SendReplyTool
{
    [McpServerTool(Name = ChannelProtocol.SendReplyTool)]
    [Description("Speak a response chunk on the originating voice satellite")]
    public static async Task<string> McpRun(
        [Description("Satellite ID owning the conversation")] string conversationId,
        [Description("Response content")] string content,
        [Description("Kind of chunk being sent")] ReplyContentType contentType,
        [Description("Whether this is the final chunk")] bool isComplete,
        [Description("Message ID for grouping related chunks")] string? messageId,
        IServiceProvider services)
    {
        var p = new SendReplyParams
        {
            ConversationId = conversationId,
            Content = content,
            ContentType = contentType,
            IsComplete = isComplete,
            MessageId = messageId
        };

        var speaker = services.GetRequiredService<ReplySpeaker>();
        var manager = services.GetRequiredService<VoiceConversationManager>();

        var satelliteId = manager.ResolveSatelliteId(p.ConversationId);
        var session = satelliteId is null
            ? null
            : services.GetRequiredService<SatelliteSessionRegistry>().Get(satelliteId);
        if (session is not null)
        {
            speaker.SpeakUtteranceReply(session, p);
            return "ok";
        }

        // No live session: the answer was written for a satellite that was not listening, so it is
        // delivered as an announcement instead. A conversation with no binding but a satellite the
        // manager still maps is one create_conversation acknowledged while a live session owned it
        // (recording a binding there would let its expiry flush the accumulator mid-turn); if that
        // session died before the reply arrived, the mapping is the fallback announce target —
        // never a silent drop that returns ok.
        var delivery = services.GetRequiredService<VoiceDeliveryRegistry>();
        var target = delivery.Resolve(p.ConversationId)
            ?? (satelliteId is null ? null : new AnnounceTarget { SatelliteId = satelliteId });
        if (target is not null)
        {
            await speaker.DeliverScheduledAsync(
                p, target, delivery, services.GetRequiredService<AnnouncementService>());
        }

        return "ok";
    }
}