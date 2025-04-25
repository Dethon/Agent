using Domain.DTOs;

namespace Domain.Tools.Attachments;

public class SearchHistory
{
    public Dictionary<int, SearchResult> History { get; private set; } = [];
    private readonly Lock _lLock = new();

    public void Add(IEnumerable<SearchResult> results)
    {
        var resultDict = results
            .ToLookup(x => x.Id, x => x)
            .ToDictionary(x => x.Key, x => x.First());
        lock (_lLock)
        {
            History = History
                .Concat(resultDict)
                .ToLookup(x => x.Key, x => x.Value)
                .ToDictionary(x => x.Key, x => x.Last());
        }
    }
}