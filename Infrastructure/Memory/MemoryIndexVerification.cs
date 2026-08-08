using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NRedisStack.RedisStackCommands;
using StackExchange.Redis;

namespace Infrastructure.Memory;

// A live index of the wrong width answers every query with an error, which recall's
// catch-all swallows, so memory silently returns nothing on every turn while everything
// still looks fine from outside. This runs at startup rather than on first recall for
// exactly that reason: a lazy check would be swallowed by the catch-all it exists to defeat.
public sealed class MemoryIndexVerification(
    IConnectionMultiplexer redis,
    string indexName,
    int configuredDimension,
    ILogger<MemoryIndexVerification> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var live = await ReadLiveIndexAsync();
        if (!live.Exists)
        {
            logger.LogInformation(
                "Memory index {Index} does not exist yet; it will be created at {Dimension} dimensions",
                indexName, configuredDimension);
            return;
        }

        // An index that exists but carries no vector field is not one the store will fix:
        // it only creates an index when reading the live one fails, so this would sit there
        // failing every search into recall's catch-all.
        var liveDimension = live.Dimension
            ?? throw new InvalidOperationException(
                $"Memory index '{indexName}' exists but has no vector field to compare against " +
                $"the configured embedding dimension of {configuredDimension}. Drop the index and " +
                "let it be recreated, or rebuild it against the current schema.");

        if (liveDimension != configuredDimension)
        {
            throw new InvalidOperationException(
                $"Memory index '{indexName}' has a {liveDimension}-dimension vector field but the " +
                $"configured embedding dimension is {configuredDimension}. Re-embed the stored " +
                "memories and rebuild the index, or point the configuration back at the model that " +
                "produced the live index.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Only the vector field's dimension is read. The live production index carries a tag
    // field the code no longer creates, left behind when a superseding feature was removed,
    // so comparing whole schemas would fail on day one against a perfectly healthy index.
    private async Task<(bool Exists, int? Dimension)> ReadLiveIndexAsync()
    {
        try
        {
            var info = await redis.GetDatabase().FT().InfoAsync(indexName);
            return (true, info.Attributes
                .Select(DimensionOf)
                .FirstOrDefault(dimension => dimension is not null));
        }
        catch (RedisServerException)
        {
            return (false, null);
        }
    }

    private static int? DimensionOf(Dictionary<string, RedisResult> attribute)
    {
        return attribute.TryGetValue("dim", out var dim) && int.TryParse(dim.ToString(), out var parsed)
            ? parsed
            : null;
    }
}