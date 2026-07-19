using System.Net.Http.Json;
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
    IHttpClientFactory httpClientFactory,
    ILogger<InsistentAnnouncementController> logger) : IInsistentAnnouncer
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

        var offlineIds = targetIds.Where(id => sessions.Get(id) is null).ToList();
        await Task.WhenAll(offlineIds.Select(id => SafePublishAsync(new VoiceEvent
        {
            Metric = VoiceMetric.AlarmOffline,
            SatelliteId = id,
            Room = registry.GetById(id)?.Room,
            Identity = registry.GetById(id)?.Identity,
            Outcome = "offline"
        })));

        if (offlineIds.Count == targetIds.Count)
        {
            // The alarm never rang — exactly when the phone must find out. rounds=0 marks
            // "never spoken"; fire-and-forget so the caller's HTTP response isn't held by the POST.
            _ = Task.Run(() => TryEscalateAsync(request, targetIds, 0));
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
                var gain = plan.GainFor(round);
                foreach (var session in OnlineSessions(targetIds))
                {
                    await session.EnqueuePlaybackAsync(
                        BuildJob(announcementId, buffered, session, gain), settings.Announce.QueueMaxDepth);
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

                // The alert is finished — take it out of the registry before the potentially slow
                // escalation webhook so a wake during the POST can't dismiss a dead alarm into snooze
                // context (Discard is idempotent, so the finally's safety-net call is a no-op below).
                alerts.Discard(handle);
                await TryEscalateAsync(request, targetIds, round);
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
        var voice = request.Voice ?? settings.Tts.OpenAi.Voice;
        var options = new SynthesisOptions { Voice = voice };
        var chunks = new List<AudioChunk> { AlarmTone.Chunk(request.Kind) };
        await foreach (var chunk in tts.SynthesizeAsync(request.Text, options, ct))
        {
            chunks.Add(chunk);
        }
        return chunks;
    }

    private PlaybackJob BuildJob(
        string announcementId, IReadOnlyList<AudioChunk> buffered, SatelliteSession session, double gain) =>
        new(
            Label: $"alarm:{announcementId}",
            Priority: AnnouncePriority.High,
            Audio: Replay(buffered, gain),
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

    private static async IAsyncEnumerable<AudioChunk> Replay(IReadOnlyList<AudioChunk> chunks, double gain)
    {
        foreach (var chunk in chunks)
        {
            yield return chunk with { Data = PcmGain.Apply(chunk.Data, gain) };
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

    // Ack-gated escalation: an unacknowledged ALARM (never a timer) is handed to HA via webhook so an
    // automation can notify another channel. Fire-and-forget: failures are logged, never retried.
    private async Task TryEscalateAsync(AnnounceRequest request, IReadOnlyList<string> targetIds, int rounds)
    {
        var url = settings.Announce.Escalation.WebhookUrl;
        if (request.Kind != AnnounceKind.Alarm || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            var client = httpClientFactory.CreateClient(nameof(InsistentAnnouncementController));
            using var response = await client.PostAsJsonAsync(
                url, new { text = request.Text, satellites = targetIds, rounds });
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Alarm escalation webhook returned {Status}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Alarm escalation webhook failed");
        }
    }
}