using System.Collections.Concurrent;
using Domain.DTOs;

namespace Domain.Tools.Attachments;

public class SearchHistory
{
    public ConcurrentDictionary<int, SearchResult> History { get; private set; } = [];

    public void Add(IEnumerable<SearchResult> results)
    {
        var resultDict = results
            .ToLookup(x => x.Id, x => x)
            .ToDictionary(x => x.Key, x => x.First());

        History = new ConcurrentDictionary<int, SearchResult>(History
            .Concat(resultDict)
            .ToLookup(x => x.Key, x => x.Value)
            .ToDictionary(x => x.Key, x => x.Last()));
    }
}