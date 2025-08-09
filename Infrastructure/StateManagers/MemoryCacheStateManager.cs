using Domain.Contracts;

namespace Infrastructure.StateManagers;

public class StateManager(
    TrackedDownloadsManager trackedDownloadsManager,
    SearchResultsManager searchResultsManager,
    SubscribedResourcesManager subscribedResourcesManager) : IStateManager
{
    public ITrackedDownloadsManager TrackedDownloads { get; } = trackedDownloadsManager;
    public ISearchResultsManager SearchResults { get; } = searchResultsManager;
    public ISubscribedResourcesManager SubscribedResources { get; } = subscribedResourcesManager;
}