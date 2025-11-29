using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

public class JackettSearchClientTests : IClassFixture<JackettFixture>
{
    private readonly JackettFixture _fixture;

    public JackettSearchClientTests(JackettFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Search_WithEmptyQuery_ReturnsEmptyResults()
    {
        // Arrange
        var client = _fixture.CreateClient();

        // Act
        var results = await client.Search("", CancellationToken.None);

        // Assert
        results.ShouldNotBeNull();
        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_WithNoIndexersConfigured_ReturnsEmptyResults()
    {
        // Arrange
        var client = _fixture.CreateClient();
        var query = "ubuntu";

        // Act
        var results = await client.Search(query, CancellationToken.None);

        // Assert - Without indexers configured, search returns empty
        results.ShouldNotBeNull();
        results.ShouldBeEmpty();
    }
}