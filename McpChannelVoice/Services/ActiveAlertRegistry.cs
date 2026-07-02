using Domain.DTOs.Voice;

namespace McpChannelVoice.Services;

public sealed record DismissedAlert(string Text, AnnounceKind Kind);

public sealed class AlertHandle
{
    private readonly CancellationTokenSource _cts;

    public AlertHandle(CancellationTokenSource cts, IReadOnlyList<string> satelliteIds, string text, AnnounceKind kind)
    {
        ArgumentNullException.ThrowIfNull(cts);
        ArgumentNullException.ThrowIfNull(satelliteIds);
        ArgumentNullException.ThrowIfNull(text);
        _cts = cts;
        SatelliteIds = satelliteIds;
        Text = text;
        Kind = kind;
    }

    public IReadOnlyList<string> SatelliteIds { get; }
    public string Text { get; }
    public AnnounceKind Kind { get; }
    public CancellationToken Token => _cts.Token;
    public bool IsAcknowledged { get; private set; }

    public void Acknowledge()
    {
        IsAcknowledged = true;
        _cts.Cancel();
    }
}

// Maps each targeted satellite id to EVERY alert covering it. Acknowledging a satellite cancels all
// of its active alerts (one wake dismisses everything ringing there — the Alexa "stop" model); each
// alert's shared CTS also stops it on its sibling satellites. Returns what was dismissed so the
// caller can hand the descriptions to the snooze context flow.
public sealed class ActiveAlertRegistry
{
    private readonly Dictionary<string, List<AlertHandle>> _bySatellite = new();
    private readonly Lock _gate = new();

    public void Register(AlertHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        lock (_gate)
        {
            foreach (var id in handle.SatelliteIds)
            {
                if (!_bySatellite.TryGetValue(id, out var handles))
                {
                    handles = [];
                    _bySatellite[id] = handles;
                }
                handles.Add(handle);
            }
        }
    }

    public IReadOnlyList<DismissedAlert> Acknowledge(string satelliteId)
    {
        List<AlertHandle> acknowledged;
        lock (_gate)
        {
            if (!_bySatellite.TryGetValue(satelliteId, out var handles))
            {
                return [];
            }
            acknowledged = handles.ToList();
        }

        // Acknowledge/Discard outside the lock: Acknowledge cancels a CTS whose continuations may
        // re-enter the registry (Discard from the alert loop's finally).
        foreach (var handle in acknowledged)
        {
            handle.Acknowledge();
            Discard(handle);
        }
        return acknowledged.Select(h => new DismissedAlert(h.Text, h.Kind)).ToList();
    }

    public void Discard(AlertHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        lock (_gate)
        {
            foreach (var id in handle.SatelliteIds)
            {
                if (!_bySatellite.TryGetValue(id, out var handles))
                {
                    continue;
                }
                handles.Remove(handle);
                if (handles.Count == 0)
                {
                    _bySatellite.Remove(id);
                }
            }
        }
    }
}