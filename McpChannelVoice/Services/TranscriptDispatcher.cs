using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using Mcp.Hosting;
using McpChannelVoice.Services.LocalCommands;

namespace McpChannelVoice.Services;

public sealed class TranscriptDispatcher(
    ChannelNotificationEmitter emitter,
    IMetricsPublisher publisher,
    VoiceConversationManager manager,
    LocalCommandDispatcher localCommands,
    ReplyTextAccumulator accumulator,
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

            PublishUtteranceEvent(
                session, transcript, similarity, stats, "dropped",
                manager.GetActiveConversationId(session.SatelliteId));
            return false;
        }

        // Local speaker commands are answered here and never reach the agent. Placed AFTER the
        // quality gate (garbage audio must not move a volume knob) and BEFORE GetOrCreateAsync,
        // which is a full create_conversation MCP round trip — matching first keeps the path fast
        // and keeps these out of conversation history.
        if (await localCommands.TryHandleAsync(transcript.Text, session, ct) is { } command)
        {
            logger.LogInformation(
                "Local command {Command} for {Satellite}: sent={Sent}",
                command.Command, session.SatelliteId, command.Sent);

            PublishUtteranceEvent(
                session, transcript, similarity, stats, command.Sent ? "command" : "command_failed",
                manager.GetActiveConversationId(session.SatelliteId));

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

        // Voice mints the key rather than letting the agent's conversation group do it, because
        // voice is the side that has to know the value in advance: the answer comes back here and
        // has to be recognised as this turn's.
        var turnKey = TurnKey.Mint();

        // Whatever the previous turn left buffered for this conversation is dropped as the next one
        // is dispatched. Doing it here rather than on a mismatched chunk clears the buffer even when
        // the abandoned run never sends anything else, which is the case that used to glue a stale
        // sentence to the front of a fresh answer.
        accumulator.Flush(conversationId);
        session.Turn.StampTurnKey(turnKey);

        // Location, SatelliteId and DismissedAlert are ordinary named properties on the shared
        // payload. Two of them are adjacent optional strings, which a positional call could
        // transpose with no compiler complaint.
        await emitter.EmitAsync(
            new ChannelMessageNotification
            {
                ConversationId = conversationId,
                Sender = sender,
                Content = transcript.Text,
                AgentId = agentId,
                Location = session.Config.DisplayLocation,
                SatelliteId = session.SatelliteId,
                DismissedAlert = dismissedAlert,
                TurnKey = turnKey,
                Timestamp = DateTimeOffset.UtcNow
            },
            ct);

        PublishUtteranceEvent(session, transcript, similarity, stats, "dispatched", conversationId);
        return true;
    }

    // Every UtteranceTranscribed publish shares this shape; only the outcome label and the
    // conversation id (active vs. newly created vs. none) differ per call site.
    private void PublishUtteranceEvent(
        SatelliteSession session,
        TranscriptionResult transcript,
        double? similarity,
        CaptureStats? stats,
        string outcome,
        string? conversationId) =>
        publisher.Publish(
            new VoiceEvent
            {
                Metric = VoiceMetric.UtteranceTranscribed,
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
            }.About(session));
}