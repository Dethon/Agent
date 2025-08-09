using Domain.Contracts;

namespace Infrastructure.StateManagers;

public class StateManager(
    ITrackedDownloadsManager trackedDownloadsManager,
    ISearchResultsManager searchResultsManager) : IStateManager
{
    public ITrackedDownloadsManager TrackedDownloads { get; } = trackedDownloadsManager;
    public ISearchResultsManager SearchResults { get; } = searchResultsManager;
}