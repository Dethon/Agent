using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;

namespace McpChannelVoice.Services;

public sealed class TranscriptDispatcher(
    ChannelNotificationEmitter emitter,
    IMetricsPublisher publisher,
    VoiceConversationManager manager,
    double avgLogProbThreshold,
    double noSpeechProbThreshold,
    TimeProvider timeProvider,
    ILogger<TranscriptDispatcher> logger)
{
    public async Task<bool> DispatchAsync(
        SatelliteSession session,
        TranscriptionResult transcript,
        string? agentId,
        CaptureStats? stats,
        double? similarity,
        string? identifiedSpeaker,
        CancellationToken ct)
    {
        // Lemonade emits no whisper score, so Confidence is never populated; the gibberish gate
        // thresholds the raw quality signals instead. Null signals fail open — a backend that
        // stops emitting them degrades to dispatch-everything, never to drop-everything.
        // Thresholds resolve per satellite (rooms differ in noise floor), falling back to globals.
        var avgLogProbFloor = session.Config.ResolveAvgLogProbThreshold(avgLogProbThreshold);
        var noSpeechProbCeiling = session.Config.ResolveNoSpeechProbThreshold(noSpeechProbThreshold);
        var lowQuality = (transcript.AvgLogProb is { } lp && lp < avgLogProbFloor)
                         || (transcript.NoSpeechProb is { } np && np > noSpeechProbCeiling);
        if (string.IsNullOrWhiteSpace(transcript.Text) || lowQuality)
        {
            logger.LogInformation(
                "Dropping transcript for {Satellite}: empty={Empty} lowQuality={LowQuality} avg_logprob={AvgLogProb} no_speech_prob={NoSpeechProb}",
                session.SatelliteId,
                string.IsNullOrWhiteSpace(transcript.Text),
                lowQuality,
                transcript.AvgLogProb,
                transcript.NoSpeechProb);

            await publisher.PublishAsync(
                new VoiceEvent
                {
                    Metric = VoiceMetric.UtteranceTranscribed,
                    SatelliteId = session.SatelliteId,
                    Room = session.Config.Room,
                    Identity = session.Config.Identity,
                    Outcome = "dropped",
                    Confidence = transcript.Confidence,
                    Similarity = similarity,
                    AvgLogProb = transcript.AvgLogProb,
                    NoSpeechProb = transcript.NoSpeechProb,
                    CompressionRatio = transcript.CompressionRatio,
                    PeakRms = stats?.PeakRms,
                    SpeechMs = stats?.SpeechMs,
                    FloorRms = stats?.FloorRms,
                    TrailingRms = stats?.TrailingRms,
                    EndReason = stats?.EndReason,
                    ConversationId = manager.GetActiveConversationId(session.SatelliteId)
                },
                ct);
            return false;
        }

        // AgentId is required by CreateConversationParams; the dispatch path always supplies one in
        // production, so the null-coalesce is only a defensive fallback (not expected at runtime).
        var conversationId = await manager.GetOrCreateAsync(session, agentId ?? string.Empty, transcript.Text, ct);

        var dismissedAlert = session.TryConsumeDismissedAlert(timeProvider.GetUtcNow());

        // A conclusive speaker match routes the enrolled person's identity into the Sender (so
        // ChatMonitor keys memory/personalization per person); a doubtful/absent match falls back to
        // the satellite's default identity. Telemetry below keeps Identity = the satellite identity.
        var sender = identifiedSpeaker ?? session.Config.Identity;

        await emitter.EmitMessageNotificationAsync(
            conversationId,
            sender,
            transcript.Text,
            agentId,
            session.Config.DisplayLocation,
            session.SatelliteId,
            dismissedAlert,
            ct);

        await publisher.PublishAsync(
            new VoiceEvent
            {
                Metric = VoiceMetric.UtteranceTranscribed,
                SatelliteId = session.SatelliteId,
                Room = session.Config.Room,
                Identity = session.Config.Identity,
                Outcome = "dispatched",
                Confidence = transcript.Confidence,
                Similarity = similarity,
                AvgLogProb = transcript.AvgLogProb,
                NoSpeechProb = transcript.NoSpeechProb,
                CompressionRatio = transcript.CompressionRatio,
                PeakRms = stats?.PeakRms,
                SpeechMs = stats?.SpeechMs,
                FloorRms = stats?.FloorRms,
                TrailingRms = stats?.TrailingRms,
                EndReason = stats?.EndReason,
                ConversationId = conversationId
            },
            ct);
        return true;
    }
}