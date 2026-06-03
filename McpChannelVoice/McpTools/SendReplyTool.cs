using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
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
        var sessions = services.GetRequiredService<SatelliteSessionRegistry>();
        var manager = services.GetRequiredService<VoiceConversationManager>();
        var accumulator = services.GetRequiredService<ReplyTextAccumulator>();
        var tts = services.GetRequiredService<ITextToSpeech>();
        var settings = services.GetRequiredService<VoiceSettings>();
        var metrics = services.GetRequiredService<IMetricsPublisher>();

        var satelliteId = manager.ResolveSatelliteId(conversationId);
        var session = satelliteId is null ? null : sessions.Get(satelliteId);
        if (session is null)
        {
            return "ok";
        }

        switch (contentType)
        {
            case ReplyContentType.Reasoning:
            case ReplyContentType.ToolCall:
                return "ok";

            case ReplyContentType.Error:
                await SpeakAsync(session, $"Hubo un error: {content}", conversationId, tts, settings, metrics, default);
                return "ok";

            // Completion arrives as a dedicated StreamComplete event (empty content, no
            // messageId). Text chunks are never flagged complete, so this is where we
            // speak the accumulated reply.
            case ReplyContentType.StreamComplete:
                await FlushAndSpeakAsync(session, accumulator, conversationId, tts, settings, metrics);
                return "ok";

            default:
                accumulator.Append(conversationId, content);
                // Defensive: honor an explicitly-completed text chunk if a transport ever sends one.
                if (isComplete)
                {
                    await FlushAndSpeakAsync(session, accumulator, conversationId, tts, settings, metrics);
                }
                return "ok";
        }
    }

    private static async Task FlushAndSpeakAsync(
        SatelliteSession session,
        ReplyTextAccumulator accumulator,
        string conversationId,
        ITextToSpeech tts,
        VoiceSettings settings,
        IMetricsPublisher metrics)
    {
        var text = accumulator.Flush(conversationId);
        if (!string.IsNullOrWhiteSpace(text))
        {
            await SpeakAsync(session, text, conversationId, tts, settings, metrics, default);
        }
    }

    private static async Task SpeakAsync(
        SatelliteSession session,
        string text,
        string conversationId,
        ITextToSpeech tts,
        VoiceSettings settings,
        IMetricsPublisher metrics,
        CancellationToken ct)
    {
        var voice = session.Config.Tts?.Wyoming?.Voice ?? settings.Tts.Wyoming?.Voice;
        var options = new SynthesisOptions { Voice = voice };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var job = new PlaybackJob(
            Label: $"reply:{session.SatelliteId}",
            Priority: AnnouncePriority.Normal,
            Audio: tts.SynthesizeAsync(text, options, ct),
            OnStarted: async _ =>
            {
                await metrics.PublishAsync(new VoiceEvent
                {
                    Metric = VoiceMetric.WakeToFirstAudioMs,
                    SatelliteId = session.SatelliteId,
                    Room = session.Config.Room,
                    Identity = session.Config.Identity,
                    DurationMs = sw.ElapsedMilliseconds,
                    ConversationId = conversationId
                });
            },
            OnPreempted: async _ =>
            {
                await metrics.PublishAsync(new VoiceEvent
                {
                    Metric = VoiceMetric.AnnouncePreemptedReply,
                    SatelliteId = session.SatelliteId,
                    ConversationId = conversationId
                });
            });

        await session.EnqueuePlaybackAsync(job, settings.Announce.QueueMaxDepth);

        await metrics.PublishAsync(new VoiceEvent
        {
            Metric = VoiceMetric.TtsLatencyMs,
            SatelliteId = session.SatelliteId,
            Room = session.Config.Room,
            Identity = session.Config.Identity,
            DurationMs = sw.ElapsedMilliseconds,
            ConversationId = conversationId
        });
    }
}