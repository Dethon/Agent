using System.Collections.Concurrent;

namespace McpChannelVoice.Services;

public sealed class AlertHandle
{
    private readonly CancellationTokenSource _cts;

    public AlertHandle(CancellationTokenSource cts, IReadOnlyList<string> satelliteIds)
    {
        ArgumentNullException.ThrowIfNull(cts);
        ArgumentNullException.ThrowIfNull(satelliteIds);
        _cts = cts;
        SatelliteIds = satelliteIds;
    }

    public IReadOnlyList<string> SatelliteIds { get; }
    public CancellationToken Token => _cts.Token;
    public bool IsAcknowledged { get; private set; }

    public void Acknowledge()
    {
        IsAcknowledged = true;
        _cts.Cancel();
    }
}

// Maps each targeted satellite id to the alert covering it. The first acknowledgment on ANY of an
// alert's satellites cancels the whole alert (shared CTS) and removes every entry for it, so a later
// wake on a sibling satellite is a no-op.
public sealed class ActiveAlertRegistry
{
    private readonly ConcurrentDictionary<string, AlertHandle> _bySatellite = new();

    public void Register(AlertHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        foreach (var id in handle.SatelliteIds)
        {
            _bySatellite[id] = handle;
        }
    }

    public bool Acknowledge(string satelliteId)
    {
        if (!_bySatellite.TryGetValue(satelliteId, out var handle))
        {
            return false;
        }
        handle.Acknowledge();
        Discard(handle);
        return true;
    }

    public void Discard(AlertHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        foreach (var id in handle.SatelliteIds)
        {
            // Remove only if this id still points at THIS handle — a newer alert may already own it.
            _bySatellite.TryRemove(new KeyValuePair<string, AlertHandle>(id, handle));
        }
    }
}