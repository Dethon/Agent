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
        IServiceProvider services,
        // Both defaulted, which is what makes them optional on the wire rather than merely
        // nullable: a required parameter would make every reply from an agent that predates them
        // an error, and this server is deployed independently of that agent.
        [Description("Key of the turn this reply answers")] string? turnKey = null,
        [Description("Whether the turn this reply answers was agent-initiated")] bool? agentInitiated = null)
    {
        var p = new SendReplyParams
        {
            ConversationId = conversationId,
            Content = content,
            ContentType = contentType,
            IsComplete = isComplete,
            MessageId = messageId,
            TurnKey = turnKey,
            AgentInitiated = agentInitiated
        };

        var speaker = services.GetRequiredService<ReplySpeaker>();
        var manager = services.GetRequiredService<VoiceConversationManager>();

        var delivery = services.GetRequiredService<VoiceDeliveryRegistry>();
        var satelliteId = manager.ResolveSatelliteId(p.ConversationId);
        var session = satelliteId is null
            ? null
            : services.GetRequiredService<SatelliteSessionRegistry>().Get(satelliteId);
        if (session is not null)
        {
            // A live session owns this conversation, which supersedes any delivery binding left over
            // from an announce that landed while the satellite happened to be disconnected: that
            // binding is now unreachable — this branch answers every reply — and its expiry would
            // flush the shared accumulator in the middle of the turn being spoken here. Dropping it
            // is the same rule create_conversation applies when it declines to bind at all.
            delivery.Remove(p.ConversationId);
            speaker.SpeakUtteranceReply(session, p);
            return "ok";
        }

        // No live session: the answer was written for a satellite that was not listening, so it is
        // delivered as an announcement instead. A conversation with no binding but a satellite the
        // manager still maps is one create_conversation acknowledged while a live session owned it
        // (recording a binding there would let its expiry flush the accumulator mid-turn); if that
        // session died before the reply arrived, the mapping is the fallback announce target —
        // never a silent drop that returns ok.
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