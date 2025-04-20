namespace Domain.Tools.Attachments;

public class SearchHistory
{
    public Dictionary<int, SearchResult> History { get; private set; } = [];
    private readonly Lock _lLock = new();

    public void Add(IEnumerable<SearchResult> results)
    {
        lock (_lLock)
        {
            History = History
                .Concat(results.ToDictionary(x => x.Id, x => x))
                .ToLookup(x => x.Key, x => x.Value)
                .ToDictionary(x => x.Key, x => x.First());
        }
    }
}