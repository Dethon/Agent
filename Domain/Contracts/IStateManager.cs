namespace Domain.Contracts;

public interface IStateManager
{
    ISearchResultsManager SearchResults { get; }
    ITrackedDownloadsManager TrackedDownloads { get; }
}