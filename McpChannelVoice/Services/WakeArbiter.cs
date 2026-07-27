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

    // Bound every re-arm write. WyomingWriter.WriteAsync on a half-open TCP socket BLOCKS rather
    // than throwing, so an unbounded write would stall the decision task with the remaining losers
    // still live and answering — the exact failure this feature exists to prevent, reached without
    // any exception for the catch to see. A re-arm is a few bytes to a LAN satellite, so anything
    // slower is a dead peer, and abandoning the write costs nothing: a reconnecting satellite
    // re-arms itself. Deliberately a constant, not a config knob — a liveness backstop is not a
    // tuning parameter.
    private const int ReArmWriteTimeoutMs = 2000;

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
            // Only clear a window we never took. Once claims is non-null the slot is already ours-
            // cleared, so any window there now belongs to a claim that arrived during this decision
            // and has its own decision task pending — nulling it would silently drop that wake.
            if (claims is null)
            {
                lock (_gate)
                {
                    _window = null;
                }
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
            // Isolate each loser: one satellite failing to be suppressed must never cost the
            // others theirs, because every un-suppressed loser is a satellite that answers.
            // Rule B still has to run afterwards, so nothing here may escape this loop.
            try
            {
                await SuppressAsync(handles[loser.Claim.SatelliteId], loser.Claim, "lost_loudness");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to suppress arbitration loser {Id}",
                    loser.Claim.SatelliteId);
            }
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
            // Commit the recoverable half BEFORE the wire write. The abort above is irreversible and
            // TransferBinding is a lock-guarded dictionary swap with no I/O, so pairing them keeps
            // the handoff atomic: a re-arm that fails or times out then costs the holder only a
            // silent re-arm, not the user's conversation continuity. Written the other way round, a
            // dead holder socket left the capture abandoned, the conversation stranded on the
            // holder until idle expiry, and no WakeHandoff recorded at all.
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
            await SendReArmAsync(holderHandle);
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
        // Metric before the wire write, for the same reason the steal transfers first: the abort is
        // already irreversible, so the record of what happened must not hinge on reaching the peer.
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
        await SendReArmAsync(handle);
    }

    // Best-effort by design: the satellite's capture is already settled locally, so the pause is
    // only what stops it streaming into a turn it lost. Failing to deliver it degrades that
    // satellite to its own timeout, which is survivable — losing the whole decision to it is not.
    private async Task SendReArmAsync(WakeArbiterHandle handle)
    {
        using var cts = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(ReArmWriteTimeoutMs), time);
        try
        {
            await (handle.Session.SupportsPause
                ? handle.PauseAsync(cts.Token)
                : handle.EndLegacyAsync(cts.Token));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Re-arm write to satellite {Id} failed or timed out",
                handle.Session.SatelliteId);
        }
    }

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