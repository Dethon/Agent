using Domain.DTOs;

namespace Domain.Contracts;

public interface ISearchClient
{
    Task<SearchResult[]> Search(string query, CancellationToken cancellationToken = default);
}