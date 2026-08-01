using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.LocalCommands;
using McpChannelVoice.Services.WyomingProtocol;

namespace McpChannelVoice.Services;

public sealed class TranscriptDispatcher(
    ChannelNotificationEmitter emitter,
    IMetricsPublisher publisher,
    VoiceConversationManager manager,
    VoiceCommandMatcher matcher,
    double avgLogProbThreshold,
    double noSpeechProbThreshold,
    double shortSpeechAvgLogProbThreshold,
    int fullThresholdSpeechMs,
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
        //
        // The avg_logprob floor loosens below FullThresholdSpeechMs of speech: a short command
        // scores lower than a long one for reasons unrelated to being wrong, so one floor drops
        // correct short turns first. Absent capture stats keep the full floor.
        var fullSpeechMs = session.Config.ResolveSttFullThresholdSpeechMs(fullThresholdSpeechMs);
        var avgLogProbFloor = stats is { } capture && capture.SpeechMs < fullSpeechMs
            ? session.Config.ResolveShortSpeechAvgLogProbThreshold(shortSpeechAvgLogProbThreshold)
            : session.Config.ResolveAvgLogProbThreshold(avgLogProbThreshold);
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

            await PublishUtteranceEventAsync(
                session, transcript, similarity, stats, "dropped",
                manager.GetActiveConversationId(session.SatelliteId), ct);
            return false;
        }

        // Local speaker commands are answered here and never reach the agent. Placed AFTER the
        // quality gate (garbage audio must not move a volume knob) and BEFORE GetOrCreateAsync,
        // which is a full create_conversation MCP round trip — matching first keeps the path fast
        // and keeps these out of conversation history.
        if (matcher.Match(transcript.Text) is { } command)
        {
            var action = command switch
            {
                VoiceCommand.LocalVolumeUp => "up",
                VoiceCommand.LocalVolumeDown => "down",
                VoiceCommand.LocalMute => "mute",
                VoiceCommand.LocalUnmute => "unmute",
                _ => null
            };

            var sent = action is not null && await session.TrySendControlAsync(
                WyomingEvent.Header("speaker-volume", new JsonObject { ["action"] = action }), ct);

            logger.LogInformation(
                "Local command {Command} for {Satellite}: sent={Sent}", command, session.SatelliteId, sent);

            await PublishUtteranceEventAsync(
                session, transcript, similarity, stats, sent ? "command" : "command_failed",
                manager.GetActiveConversationId(session.SatelliteId), ct);

            // False means "nothing reached the agent", which FollowUpConversation already turns into
            // EndConversation — the satellite gets its closing transcript and re-arms. No new
            // turn-end path is needed.
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

        await PublishUtteranceEventAsync(session, transcript, similarity, stats, "dispatched", conversationId, ct);
        return true;
    }

    // Every UtteranceTranscribed publish shares this shape; only the outcome label and the
    // conversation id (active vs. newly created vs. none) differ per call site.
    private Task PublishUtteranceEventAsync(
        SatelliteSession session,
        TranscriptionResult transcript,
        double? similarity,
        CaptureStats? stats,
        string outcome,
        string? conversationId,
        CancellationToken ct) =>
        publisher.PublishAsync(
            new VoiceEvent
            {
                Metric = VoiceMetric.UtteranceTranscribed,
                SatelliteId = session.SatelliteId,
                Room = session.Config.Room,
                Identity = session.Config.Identity,
                Outcome = outcome,
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
}