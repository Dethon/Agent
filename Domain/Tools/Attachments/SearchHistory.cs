using System.Collections.Concurrent;
using Domain.DTOs;

namespace Domain.Tools.Attachments;

public class SearchHistory
{
    public ConcurrentDictionary<int, SearchResult> History { get; } = [];

    public void Add(IEnumerable<SearchResult> results)
    {
        foreach (var result in results.GroupBy(x => x.Id))
        {
            History[result.Key] = result.First();
        }
    }
}