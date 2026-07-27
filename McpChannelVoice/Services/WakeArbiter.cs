using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public sealed record WakeArbiterHandle(
    SatelliteSession Session,
    Func<CancellationToken, Task> PauseAsync,
    Func<CancellationToken, Task> EndLegacyAsync);

// Cross-satellite wake arbitration seat (spec: docs/superpowers/specs/
// 2026-07-27-wake-arbitration-design.md). Claims arrive synchronously on each connection's
// Wyoming read loop; the decision runs later on its own task, so the read loops never wait.
// Every claimant has already opened its capture — losing costs a discarded capture, never audio.
public sealed class WakeArbiter(
    ArbitrationSettings settings,
    VoiceConversationManager conversations,
    IMetricsPublisher metrics,
    TimeProvider time,
    ILogger<WakeArbiter> logger)
{
    private readonly Dictionary<string, WakeArbiterHandle> _handles = new();
    private readonly Lock _gate = new();
    private List<WakeClaim>? _window;

    public void Register(string satelliteId, WakeArbiterHandle handle)
    {
        lock (_gate)
        {
            _handles[satelliteId] = handle;
        }
    }

    public void Unregister(string satelliteId)
    {
        lock (_gate)
        {
            _handles.Remove(satelliteId);
        }
    }

    public void Claim(string satelliteId, double? wakeRms, double? wakeScore, string source)
    {
        if (!settings.Enabled)
        {
            return;
        }
        lock (_gate)
        {
            if (_handles.Count < 2)
            {
                return;
            }
            var claim = new WakeClaim(satelliteId, wakeRms, wakeScore, source, time.GetTimestamp());
            if (_window is not null)
            {
                if (_window.All(c => c.SatelliteId != satelliteId))
                {
                    _window.Add(claim);
                }
                return;
            }
            _window = [claim];
        }
        _ = DecideAfterWindowAsync();
    }

    private async Task DecideAfterWindowAsync()
    {
        List<WakeClaim>? claims = null;
        Dictionary<string, WakeArbiterHandle> handles;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(settings.WindowMs), time);
            lock (_gate)
            {
                claims = _window;
                _window = null;
                handles = new Dictionary<string, WakeArbiterHandle>(_handles);
            }
            if (claims is not null)
            {
                await DecideAsync(claims, handles);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Wake arbitration decision failed for {Claims}",
                string.Join(", ", (claims ?? []).Select(c => c.SatelliteId)));
            lock (_gate)
            {
                _window = null;
            }
        }
    }

    private async Task DecideAsync(List<WakeClaim> claims, Dictionary<string, WakeArbiterHandle> handles)
    {
        var candidates = claims
            .Where(c => handles.ContainsKey(c.SatelliteId))
            .Select(c => new ArbitrationCandidate(c, c.WakeRms is { } rms
                ? WakeArbitrationRules.Calibrate(rms, handles[c.SatelliteId].Session.Config.RmsOffsetDb)
                : null))
            .ToList();
        // Every claimant may have disconnected inside the window, and PickWinner ends in First().
        if (candidates.Count == 0)
        {
            return;
        }

        var winner = WakeArbitrationRules.PickWinner(candidates);
        foreach (var loser in candidates.Where(c => !ReferenceEquals(c, winner)))
        {
            await SuppressAsync(handles[loser.Claim.SatelliteId], loser.Claim, "lost_loudness");
        }

        if (winner.Claim.Source == "button")
        {
            return; // deliberate physical intent: never suppressed, never a leak
        }

        var frequency = time.TimestampFrequency;
        var (spanStart, spanEnd) = WakeArbitrationRules.WakeWordSpan(
            winner.Claim.ReceivedAt, frequency, settings);
        var slack = WakeArbitrationRules.MsToTicks(settings.AlignSlackMs, frequency);
        var holder = handles
            .Where(kv => claims.All(c => c.SatelliteId != kv.Key))
            .Select(kv => (kv.Key, Handle: kv.Value, Activity: kv.Value.Session.GetCaptureActivity()))
            .Where(h => h.Activity is not null && WakeArbitrationRules.HasAlignedOnset(
                h.Activity!, spanStart, spanEnd, frequency, settings))
            .Select(h => (h.Key, h.Handle, Peak: WakeArbitrationRules.Calibrate(
                WakeArbitrationRules.SpanPeakRms(h.Activity!, spanStart - slack, spanEnd + slack),
                h.Handle.Session.Config.RmsOffsetDb)))
            .OrderByDescending(h => h.Peak)
            .Select(h => ((string, WakeArbiterHandle, double)?)h)
            .FirstOrDefault();
        if (holder is not { } aligned)
        {
            return; // no other mic heard this utterance: the winner just proceeds
        }

        var (holderId, holderHandle, holderPeak) = aligned;
        if (winner.CalibratedRms is { } challenger
            && WakeArbitrationRules.CanSteal(challenger, holderPeak, settings.StealMarginDb))
        {
            // Only a capture we actually aborted may be stolen from: if it already ended
            // naturally, its dispatch is in flight and these were independent turns.
            if (!holderHandle.Session.TryAbortCapture())
            {
                return;
            }
            await SendReArmAsync(holderHandle);
            conversations.TransferBinding(holderId, winner.Claim.SatelliteId);
            await PublishAsync(new VoiceEvent
            {
                Metric = VoiceMetric.WakeHandoff,
                SatelliteId = winner.Claim.SatelliteId,
                Room = handles[winner.Claim.SatelliteId].Session.Config.Room,
                Identity = handles[winner.Claim.SatelliteId].Session.Config.Identity,
                Outcome = holderId,
                WakeRms = winner.Claim.WakeRms,
                WakeScore = winner.Claim.WakeScore
            });
            return;
        }

        // The wake word leaked into the holder's already-open mic and the holder heard it
        // louder (or the challenger can't prove otherwise): the holder keeps the turn.
        await SuppressAsync(handles[winner.Claim.SatelliteId], winner.Claim, "leak");
    }

    private async Task SuppressAsync(WakeArbiterHandle handle, WakeClaim claim, string outcome)
    {
        if (!handle.Session.TryAbortCapture())
        {
            logger.LogWarning(
                "Arbitration loser {Id} had no abortable capture (ended early); letting it proceed",
                claim.SatelliteId);
            return;
        }
        await SendReArmAsync(handle);
        await PublishAsync(new VoiceEvent
        {
            Metric = VoiceMetric.WakeSuppressed,
            SatelliteId = claim.SatelliteId,
            Room = handle.Session.Config.Room,
            Identity = handle.Session.Config.Identity,
            Outcome = outcome,
            WakeRms = claim.WakeRms,
            WakeScore = claim.WakeScore
        });
    }

    private static Task SendReArmAsync(WakeArbiterHandle handle) =>
        handle.Session.SupportsPause
            ? handle.PauseAsync(CancellationToken.None)
            : handle.EndLegacyAsync(CancellationToken.None);

    private async Task PublishAsync(VoiceEvent evt)
    {
        try
        {
            await metrics.PublishAsync(evt, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish {Metric}", evt.Metric);
        }
    }
}