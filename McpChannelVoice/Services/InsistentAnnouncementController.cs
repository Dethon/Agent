using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

// Drives an insistent alert: plays the message (High priority) on every targeted online satellite and
// repeats on a gap until acknowledged (out-of-band, via ActiveAlertRegistry) or a safety cap. The
// satellite mics only on local wake, so there is no mic window here — acknowledgment arrives when the
// user wakes a satellite and WyomingSatelliteHost calls ActiveAlertRegistry.Acknowledge.
public sealed class InsistentAnnouncementController(
    SatelliteRegistry registry,
    SatelliteSessionRegistry sessions,
    ITextToSpeech tts,
    VoiceSettings settings,
    ActiveAlertRegistry alerts,
    IMetricsPublisher metrics,
    TimeProvider time,
    ILogger<InsistentAnnouncementController> logger)
{
    public async Task<AnnounceResponse> StartAsync(AnnounceRequest request, CancellationToken ct)
    {
        var targetIds = ResolveConfigured(request.Target);
        if (targetIds.Count == 0)
        {
            logger.LogWarning(
                "Insistent announce target not found: id={Id} room={Room} all={All}",
                request.Target.SatelliteId, request.Target.Room, request.Target.All);
            throw new AnnounceTargetNotFoundException("No matching satellites for the requested target.");
        }

        var announcementId = Guid.NewGuid().ToString("N");

        if (!targetIds.Any(id => sessions.Get(id) is not null))
        {
            await Task.WhenAll(targetIds.Select(id => SafePublishAsync(new VoiceEvent
            {
                Metric = VoiceMetric.AlarmOffline,
                SatelliteId = id,
                Room = registry.GetById(id)?.Room,
                Identity = registry.GetById(id)?.Identity,
                Outcome = "offline"
            })));
            return new AnnounceResponse
            {
                AnnouncementId = announcementId,
                Satellites = targetIds.Select(id => new AnnouncementOutcome { Id = id, Status = "offline" }).ToList()
            };
        }

        var plan = InsistentPlan.Resolve(request.Insistent, settings.Announce.Insistent);
        var handle = new AlertHandle(new CancellationTokenSource(), targetIds, request.Text, request.Kind);
        alerts.Register(handle);

        _ = Task.Run(() => RunLoopAsync(announcementId, request, plan, handle, targetIds));

        return new AnnounceResponse
        {
            AnnouncementId = announcementId,
            Satellites = targetIds.Select(id =>
                new AnnouncementOutcome { Id = id, Status = sessions.Get(id) is not null ? "started" : "offline" }).ToList()
        };
    }

    private async Task RunLoopAsync(
        string announcementId, AnnounceRequest request, InsistentPlan plan, AlertHandle handle, IReadOnlyList<string> targetIds)
    {
        try
        {
            var buffered = await BufferAudioAsync(request, handle.Token);
            var start = time.GetTimestamp();
            var round = 0;

            while (!handle.Token.IsCancellationRequested
                   && round < plan.MaxRepeats
                   && (plan.MaxDuration is not { } max || time.GetElapsedTime(start) < max))
            {
                foreach (var session in OnlineSessions(targetIds))
                {
                    await session.EnqueuePlaybackAsync(
                        BuildJob(announcementId, buffered, session), settings.Announce.QueueMaxDepth);
                }
                round++;

                // Skip the gap delay when this was the last round (cap reached or token already
                // cancelled). Doing the delay would require the test to advance fake time once more,
                // and it would defer the AlarmUnacknowledged/AlarmAcknowledged publish unnecessarily.
                var capReached = round >= plan.MaxRepeats
                    || handle.Token.IsCancellationRequested
                    || (plan.MaxDuration is { } maxLeft && time.GetElapsedTime(start) >= maxLeft);
                if (capReached)
                {
                    break;
                }

                try
                {
                    await Task.Delay(plan.Gap, time, handle.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (handle.IsAcknowledged)
            {
                foreach (var session in OnlineSessions(targetIds))
                {
                    session.PreemptCurrent();
                }
                await SafePublishAsync(AlarmEvent(VoiceMetric.AlarmAcknowledged, targetIds, round));
            }
            else
            {
                await SafePublishAsync(AlarmEvent(VoiceMetric.AlarmUnacknowledged, targetIds, round));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Insistent alert {Id} loop failed", announcementId);
        }
        finally
        {
            alerts.Discard(handle);
        }
    }

    private async Task<IReadOnlyList<AudioChunk>> BufferAudioAsync(AnnounceRequest request, CancellationToken ct)
    {
        // One synthesis per alert, replayed every round/satellite. Per-satellite voice overrides are not
        // applied to insistent alerts in v1 (single synthesis); the request voice or global voice is used.
        var voice = request.Voice ?? settings.Tts.Wyoming.Voice;
        var options = new SynthesisOptions { Voice = voice };
        var chunks = new List<AudioChunk>();
        await foreach (var chunk in tts.SynthesizeAsync(request.Text, options, ct))
        {
            chunks.Add(chunk);
        }
        return chunks;
    }

    private PlaybackJob BuildJob(string announcementId, IReadOnlyList<AudioChunk> buffered, SatelliteSession session) =>
        new(
            Label: $"alarm:{announcementId}",
            Priority: AnnouncePriority.High,
            Audio: Replay(buffered),
            OnStarted: _ => SafePublishAsync(new VoiceEvent
            {
                Metric = VoiceMetric.AnnouncePlayed,
                SatelliteId = session.SatelliteId,
                Room = session.Config.Room,
                Identity = session.Config.Identity,
                Priority = AnnouncePriority.High.ToString()
            }),
            OnPreempted: _ => Task.CompletedTask);

    private IEnumerable<SatelliteSession> OnlineSessions(IReadOnlyList<string> targetIds) =>
        targetIds.Select(sessions.Get).Where(s => s is not null).Select(s => s!);

    private VoiceEvent AlarmEvent(VoiceMetric metric, IReadOnlyList<string> targetIds, int rounds)
    {
        var first = targetIds.Count > 0 ? registry.GetById(targetIds[0]) : null;
        return new VoiceEvent
        {
            Metric = metric,
            SatelliteId = targetIds.Count > 0 ? targetIds[0] : null,
            Room = first?.Room,
            Identity = first?.Identity,
            DurationMs = rounds
        };
    }

    private IReadOnlyList<string> ResolveConfigured(AnnounceTarget target)
    {
        if (target.SatelliteIds is { Count: > 0 })
        {
            return target.SatelliteIds.Where(id => registry.GetById(id) is not null).Distinct().ToList();
        }
        if (target.SatelliteId is not null)
        {
            return registry.GetById(target.SatelliteId) is null ? [] : [target.SatelliteId];
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

    private static async IAsyncEnumerable<AudioChunk> Replay(IReadOnlyList<AudioChunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return chunk;
        }
        await Task.CompletedTask;
    }

    private async Task SafePublishAsync(VoiceEvent evt)
    {
        try
        {
            await metrics.PublishAsync(evt, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish alarm metric {Metric}", evt.Metric);
        }
    }
}