using Domain.DTOs.Voice;

namespace Domain.Contracts;

// The voice satellites an announcement can reach. Resolve owns the AnnounceTarget precedence
// (satelliteIds > satelliteId > room > all) so that create-time validation and fire-time routing
// can never disagree about what a target means.
public interface ISatelliteCatalog
{
    IReadOnlyList<SatelliteDescriptor> GetAll();

    bool Exists(string satelliteId);

    IReadOnlyList<string> Resolve(AnnounceTarget target);
}