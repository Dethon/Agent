using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public sealed class AnnounceTargetNotFoundException(string message) : Exception(message);

public class AnnouncementService(
    SatelliteRegistry registry,
    SatelliteSessionRegistry sessions,
    ITextToSpeech tts,
    VoiceSettings settings,
    IMetricsPublisher metrics,
    ILogger<AnnouncementService> logger)
{
    public async Task<AnnounceResponse> AnnounceAsync(
        AnnounceRequest request,
        string source,
        CancellationToken ct)
    {
        var targetIds = ResolveTargets(request.Target);
        if (targetIds.Count == 0)
        {
            throw new AnnounceTargetNotFoundException(
                $"No matching satellites for target: id={request.Target.SatelliteId} room={request.Target.Room} all={request.Target.All}");
        }

        var announcementId = Guid.NewGuid().ToString("N");
        var outcomes = new List<AnnouncementOutcome>();

        foreach (var id in targetIds)
        {
            var session = sessions.Get(id);
            if (session is null)
            {
                outcomes.Add(new AnnouncementOutcome { Id = id, Status = "offline" });
                await metrics.PublishAsync(new VoiceEvent
                {
                    Metric = VoiceMetric.AnnounceError,
                    SatelliteId = id,
                    Source = source,
                    Priority = request.Priority.ToString(),
                    Outcome = "offline"
                }, ct);
                continue;
            }

            var voice = request.Voice
                        ?? session.Config.Tts?.Wyoming?.Voice
                        ?? settings.Tts.Wyoming?.Voice;
            var options = new SynthesisOptions { Voice = voice };

            var job = new PlaybackJob(
                Label: $"announce:{announcementId}",
                Priority: request.Priority,
                Audio: tts.SynthesizeAsync(request.Text, options, ct),
                OnStarted: async _ =>
                {
                    await metrics.PublishAsync(new VoiceEvent
                    {
                        Metric = VoiceMetric.AnnouncePlayed,
                        SatelliteId = id,
                        Room = session.Config.Room,
                        Source = source,
                        Priority = request.Priority.ToString()
                    }, ct);
                },
                OnPreempted: async _ =>
                {
                    await metrics.PublishAsync(new VoiceEvent
                    {
                        Metric = VoiceMetric.AnnouncePreemptedReply,
                        SatelliteId = id,
                        Source = source
                    }, ct);
                });

            var accepted = await session.EnqueuePlaybackAsync(job, settings.Announce.QueueMaxDepth);
            outcomes.Add(new AnnouncementOutcome { Id = id, Status = accepted ? "queued" : "dropped" });

            await metrics.PublishAsync(new VoiceEvent
            {
                Metric = accepted ? VoiceMetric.AnnounceQueued : VoiceMetric.AnnounceError,
                SatelliteId = id,
                Room = session.Config.Room,
                Source = source,
                Priority = request.Priority.ToString(),
                Outcome = accepted ? "queued" : "dropped"
            }, ct);
        }

        logger.LogInformation("Announce {Id} -> {N} targets ({Status})",
            announcementId, outcomes.Count,
            string.Join(",", outcomes.Select(o => $"{o.Id}={o.Status}")));

        return new AnnounceResponse { AnnouncementId = announcementId, Satellites = outcomes };
    }

    private IReadOnlyList<string> ResolveTargets(AnnounceTarget target)
    {
        if (target.SatelliteId is not null)
        {
            return registry.GetById(target.SatelliteId) is null
                ? []
                : [target.SatelliteId];
        }
        if (target.Room is not null)
        {
            return registry.GetIdsByRoom(target.Room);
        }

        if (target.All == true)
        {
            return registry.GetAllIds();
        }

        return [];
    }
}