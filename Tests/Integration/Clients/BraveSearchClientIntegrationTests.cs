using Domain.Contracts;
using Infrastructure.Clients;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Tests.Integration.Clients;

public class BraveSearchClientIntegrationTests : IAsyncLifetime
{
    private readonly string? _apiKey;

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
    }

    public BraveSearchClientIntegrationTests()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<BraveSearchClientIntegrationTests>()
            .AddEnvironmentVariables()
            .Build();

        _apiKey = config["BraveSearch:ApiKey"];
    }

    private bool HasApiKey => !string.IsNullOrEmpty(_apiKey);

    [SkippableFact]
    public async Task SearchAsync_WithRealApi_ReturnsResults()
    {
        Skip.IfNot(HasApiKey, "Brave Search API key not configured");

        // Arrange
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.search.brave.com/res/v1/")
        };
        var client = new BraveSearchClient(httpClient, _apiKey!);

        // Act
        var query = new WebSearchQuery("Dune movie 2024", MaxResults: 5);
        var result = await client.SearchAsync(query);

        // Assert
        result.ShouldNotBeNull();
        result.Results.ShouldNotBeEmpty();
        result.SearchEngine.ShouldBe("brave");
        result.Results.ShouldAllBe(r => !string.IsNullOrEmpty(r.Title));
        result.Results.ShouldAllBe(r => !string.IsNullOrEmpty(r.Url));
        result.Results.ShouldAllBe(r => !string.IsNullOrEmpty(r.Domain));
    }

    [SkippableFact]
    public async Task SearchAsync_WithSiteFilter_ReturnsFilteredResults()
    {
        Skip.IfNot(HasApiKey, "Brave Search API key not configured");

        // Arrange
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.search.brave.com/res/v1/")
        };
        var client = new BraveSearchClient(httpClient, _apiKey!);

        // Act
        var query = new WebSearchQuery("Oppenheimer", MaxResults: 5, Site: "imdb.com");
        var result = await client.SearchAsync(query);

        // Assert
        result.ShouldNotBeNull();
        result.Results.ShouldNotBeEmpty();
        result.Results.ShouldAllBe(r => r.Domain.Contains("imdb.com"));
    }

    [SkippableFact]
    public async Task SearchAsync_WithDateRange_ReturnsRecentResults()
    {
        Skip.IfNot(HasApiKey, "Brave Search API key not configured");

        // Arrange
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.search.brave.com/res/v1/")
        };
        var client = new BraveSearchClient(httpClient, _apiKey!);

        // Act
        var query = new WebSearchQuery(
            "technology news",
            MaxResults: 5,
            DateRange: DateRange.Week);
        var result = await client.SearchAsync(query);

        // Assert
        result.ShouldNotBeNull();
        result.Results.ShouldNotBeEmpty();
    }
}