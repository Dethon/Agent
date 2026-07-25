using Domain.DTOs.Voice;

namespace Domain.Contracts;

// The voice satellites an announcement can reach. Resolve owns the AnnounceTarget precedence
// (satelliteIds > satelliteId > room > all) so that create-time validation and fire-time routing
// can never disagree about what a target means. Async: the roster and resolution are authoritative
// in the voice hub, which the timers server reaches over HTTP.
public interface ISatelliteCatalog
{
    Task<IReadOnlyList<SatelliteDescriptor>> GetAllAsync(CancellationToken ct);

    Task<IReadOnlyList<string>> ResolveAsync(AnnounceTarget target, CancellationToken ct);
}