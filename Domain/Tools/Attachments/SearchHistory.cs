using System.Collections.Concurrent;
using Domain.DTOs;

namespace Domain.Tools.Attachments;

public class SearchHistory
{
    public ConcurrentDictionary<int, SearchResult> History { get; } = [];

    public void Add(IEnumerable<SearchResult> results)
    {
        var resultDict = results
            .ToLookup(x => x.Id, x => x)
            .ToDictionary(x => x.Key, x => x.First());

        foreach (var result in resultDict)
        {
            History[result.Key] = result.Value;
        }
    }
}