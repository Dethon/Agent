using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;

namespace McpChannelVoice.Services;

public sealed class TranscriptDispatcher(
    ChannelNotificationEmitter emitter,
    IMetricsPublisher publisher,
    ApprovalCaptureBroker broker,
    VoiceConversationManager manager,
    double confidenceThreshold,
    ILogger<TranscriptDispatcher> logger)
{
    public async Task<bool> DispatchAsync(
        SatelliteSession session,
        TranscriptionResult transcript,
        string? agentId,
        CancellationToken ct)
    {
        if (broker.SubmitUtterance(session.SatelliteId, transcript.Text))
        {
            logger.LogInformation("Transcript routed to pending approval for {Id}", session.SatelliteId);
            return true;
        }

        var lowConfidence = transcript.Confidence is { } c && c < confidenceThreshold;
        if (string.IsNullOrWhiteSpace(transcript.Text) || lowConfidence)
        {
            logger.LogInformation(
                "Dropping transcript for {Satellite}: empty={Empty} low={Low} confidence={Conf}",
                session.SatelliteId,
                string.IsNullOrWhiteSpace(transcript.Text),
                lowConfidence,
                transcript.Confidence);

            await publisher.PublishAsync(
                new VoiceEvent
                {
                    Metric = VoiceMetric.UtteranceTranscribed,
                    SatelliteId = session.SatelliteId,
                    Room = session.Config.Room,
                    Identity = session.Config.Identity,
                    Language = transcript.Language,
                    Outcome = "dropped",
                    Confidence = transcript.Confidence,
                    ConversationId = manager.GetActiveConversationId(session.SatelliteId)
                },
                ct);
            return false;
        }

        // AgentId is required by CreateConversationParams; the dispatch path always supplies one in
        // production, so the null-coalesce is only a defensive fallback (not expected at runtime).
        var conversationId = await manager.GetOrCreateAsync(session, agentId ?? string.Empty, transcript.Text, ct);

        await emitter.EmitMessageNotificationAsync(
            new ChannelMessageNotification
            {
                ConversationId = conversationId,
                Sender = session.Config.Identity,
                Location = session.Config.Room,
                Content = transcript.Text,
                AgentId = agentId,
                Timestamp = DateTimeOffset.UtcNow
            },
            ct);

        await publisher.PublishAsync(
            new VoiceEvent
            {
                Metric = VoiceMetric.UtteranceTranscribed,
                SatelliteId = session.SatelliteId,
                Room = session.Config.Room,
                Identity = session.Config.Identity,
                Language = transcript.Language,
                Outcome = "dispatched",
                Confidence = transcript.Confidence,
                ConversationId = conversationId
            },
            ct);
        return true;
    }
}