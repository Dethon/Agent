namespace Domain.Contracts;

public interface IStateManager
{
    ISubscribedResourcesManager SubscribedResources { get; }
    ISearchResultsManager SearchResults { get; }
    ITrackedDownloadsManager TrackedDownloads { get; }
}